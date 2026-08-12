using System.Collections.Immutable;
using System.Globalization;
using Motara.Core.Parameters;

namespace Motara.Core.Formulas;

public static class SourceFormulaCompiler
{
    private const int MaximumExpressionLength = 1024;
    private const int MaximumNestingDepth = 64;
    private const int MaximumStackDepth = 128;

    public static ImmutableArray<SourceFormulaValidationDiagnostic> Validate(
        SourceFormulaProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var diagnostics = ImmutableArray.CreateBuilder<SourceFormulaValidationDiagnostic>();
        var invalidOutputs = new bool[profile.Outputs.Length];
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        bool inputsValid = true;
        foreach (string inputId in profile.InputIds)
        {
            if (string.IsNullOrWhiteSpace(inputId) || !inputs.Add(inputId))
            {
                inputsValid = false;
            }
        }

        var outputIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < profile.Outputs.Length; index++)
        {
            SourceFormulaDefinition definition = profile.Outputs[index];
            if (!GlobalParameterId.IsValid(definition.OutputId))
            {
                AddDiagnostic(index, definition.OutputId, SourceFormulaErrorCode.DuplicateIdentifier,
                    "Invalid output identifier.");
                continue;
            }

            if (!outputIndexes.TryAdd(definition.OutputId, index))
            {
                int firstIndex = outputIndexes[definition.OutputId];
                AddDiagnostic(firstIndex, definition.OutputId, SourceFormulaErrorCode.DuplicateIdentifier,
                    "Duplicate output identifier.");
                AddDiagnostic(index, definition.OutputId, SourceFormulaErrorCode.DuplicateIdentifier,
                    "Duplicate output identifier.");
            }
            else if (inputs.Contains(definition.OutputId))
            {
                AddDiagnostic(index, definition.OutputId, SourceFormulaErrorCode.DuplicateIdentifier,
                    "Input and output identifiers must not overlap.");
            }
        }

        if (!inputsValid)
        {
            diagnostics.Add(new SourceFormulaValidationDiagnostic(
                -1,
                null,
                new SourceFormulaDiagnostic(
                    SourceFormulaErrorCode.DuplicateIdentifier,
                    0,
                    0,
                    "Invalid or duplicate input identifier.")));
        }

        Dictionary<string, int> inputIndexes = inputs.ToDictionary(
            static id => id,
            static _ => 0,
            StringComparer.Ordinal);
        var dependencies = new HashSet<int>[profile.Outputs.Length];
        for (int index = 0; index < profile.Outputs.Length; index++)
        {
            dependencies[index] = [];
            if (invalidOutputs[index])
            {
                continue;
            }

            SourceFormulaDefinition definition = profile.Outputs[index];
            try
            {
                ValidateDefinition(definition);
                ExpressionNode node = new Parser(definition.Expression).Parse();
                foreach (VariableNode reference in EnumerateReferences(node))
                {
                    if (outputIndexes.TryGetValue(reference.Id, out int outputIndex))
                    {
                        dependencies[index].Add(outputIndex);
                    }
                    else if (!inputs.Contains(reference.Id))
                    {
                        throw Error(
                            SourceFormulaErrorCode.UnknownReference,
                            $"Unknown formula reference: {reference.Id}",
                            reference.Start,
                            reference.Length);
                    }
                }

                var instructions = ImmutableArray.CreateBuilder<FormulaInstruction>();
                Emit(node, inputIndexes, outputIndexes, instructions);
                _ = CalculateMaximumStackDepth(instructions);
            }
            catch (SourceFormulaCompilationException exception)
            {
                dependencies[index].Clear();
                AddDiagnostic(
                    index,
                    definition.OutputId,
                    exception.Code,
                    exception.Message,
                    exception.Start,
                    exception.Length);
            }
        }

        for (int index = 0; index < dependencies.Length; index++)
        {
            if (!invalidOutputs[index]
                && ParticipatesInCycle(index, index, dependencies, []))
            {
                AddDiagnostic(
                    index,
                    profile.Outputs[index].OutputId,
                    SourceFormulaErrorCode.CyclicDependency,
                    "Formula dependency cycle detected.");
            }
        }

        return diagnostics
            .OrderBy(diagnostic => diagnostic.OutputIndex)
            .ToImmutableArray();

        void AddDiagnostic(
            int outputIndex,
            string? outputId,
            SourceFormulaErrorCode code,
            string message,
            int start = 0,
            int length = 0)
        {
            if (outputIndex >= 0)
            {
                if (invalidOutputs[outputIndex])
                {
                    return;
                }

                invalidOutputs[outputIndex] = true;
            }

            diagnostics.Add(new SourceFormulaValidationDiagnostic(
                outputIndex,
                outputId,
                new SourceFormulaDiagnostic(code, start, length, message)));
        }
    }

    public static CompiledSourceFormulaProgram Compile(SourceFormulaProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var inputs = CreateIdentifierMap(profile.InputIds, "input");
        var outputs = CreateOutputMap(profile.Outputs);
        foreach (string inputId in inputs.Keys)
        {
            if (outputs.ContainsKey(inputId))
            {
                throw Error(
                    SourceFormulaErrorCode.DuplicateIdentifier,
                    "Input and output identifiers must not overlap.");
            }
        }

        var nodes = ImmutableArray.CreateBuilder<ExpressionNode>(profile.Outputs.Length);
        var dependencies = new HashSet<int>[profile.Outputs.Length];
        for (int index = 0; index < profile.Outputs.Length; index++)
        {
            SourceFormulaDefinition definition = profile.Outputs[index];
            ValidateDefinition(definition);
            ExpressionNode node = new Parser(definition.Expression).Parse();
            nodes.Add(node);
            dependencies[index] = [];
            foreach (VariableNode reference in EnumerateReferences(node))
            {
                if (outputs.TryGetValue(reference.Id, out int outputSlot))
                {
                    dependencies[index].Add(outputSlot);
                }
                else if (!inputs.ContainsKey(reference.Id))
                {
                    throw Error(
                        SourceFormulaErrorCode.UnknownReference,
                        $"Unknown formula reference: {reference.Id}",
                        reference.Start,
                        reference.Length);
                }
            }
        }

        ImmutableArray<int> evaluationOrder = CreateEvaluationOrder(dependencies);
        var formulas = ImmutableArray.CreateBuilder<CompiledFormula>(profile.Outputs.Length);
        foreach (ExpressionNode node in nodes)
        {
            var instructions = ImmutableArray.CreateBuilder<FormulaInstruction>();
            Emit(node, inputs, outputs, instructions);
            int maximumDepth = CalculateMaximumStackDepth(instructions);
            formulas.Add(new CompiledFormula(instructions.ToImmutable(), maximumDepth));
        }

        return new CompiledSourceFormulaProgram(
            profile.SourceId,
            profile.InputIds,
            profile.Outputs,
            formulas.ToImmutable(),
            evaluationOrder);
    }

    internal static string FormatExpression(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaximumExpressionLength)
        {
            throw Error(SourceFormulaErrorCode.InvalidDefinition, "Invalid formula definition.");
        }

        return Render(new Parser(expression).Parse(), 0, false);
    }

    private static string Render(ExpressionNode node, int parentPrecedence, bool isRightOperand)
    {
        int precedence = GetPrecedence(node);
        string text = node switch
        {
            ConstantNode constant => constant.Value.ToString("R", CultureInfo.InvariantCulture),
            VariableNode variable => variable.Id,
            UnaryNode unary => "-" + Render(unary.Operand, precedence, false),
            BinaryNode binary => RenderBinary(binary, precedence),
            FunctionNode function => RenderFunction(function),
            _ => throw new InvalidOperationException("Unknown formula node."),
        };

        return precedence < parentPrecedence
            || (isRightOperand && precedence == parentPrecedence && node is BinaryNode)
                ? $"({text})"
                : text;
    }

    private static string RenderBinary(BinaryNode binary, int precedence)
    {
        string operation = binary.Operator switch
        {
            TokenKind.Plus => "+",
            TokenKind.Minus => "-",
            TokenKind.Star => "*",
            TokenKind.Slash => "/",
            _ => throw new InvalidOperationException("Unknown binary operator."),
        };
        return $"{Render(binary.Left, precedence, false)} {operation} {Render(binary.Right, precedence, true)}";
    }

    private static string RenderFunction(FunctionNode function)
    {
        if (!SourceFormulaLanguage.TryGetFunction(function.Name, out FormulaFunctionDefinition? definition))
        {
            throw Error(
                SourceFormulaErrorCode.UnsupportedFunction,
                $"Unsupported formula function: {function.Name}",
                function.Start,
                function.Length);
        }

        if (function.Arguments.Length != definition.Arity)
        {
            throw Error(
                SourceFormulaErrorCode.Syntax,
                "Formula function arity is invalid.",
                function.Start,
                function.Length);
        }

        return $"{function.Name}({string.Join(", ", function.Arguments.Select(argument => Render(argument, 0, false)))})";
    }

    private static int GetPrecedence(ExpressionNode node) => node switch
    {
        BinaryNode { Operator: TokenKind.Plus or TokenKind.Minus } => 1,
        BinaryNode { Operator: TokenKind.Star or TokenKind.Slash } => 2,
        UnaryNode => 3,
        _ => 4,
    };

    private static Dictionary<string, int> CreateIdentifierMap(
        ImmutableArray<string> ids,
        string kind)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < ids.Length; index++)
        {
            string id = ids[index];
            if (string.IsNullOrWhiteSpace(id) || !map.TryAdd(id, index))
            {
                throw Error(
                    SourceFormulaErrorCode.DuplicateIdentifier,
                    $"Invalid or duplicate {kind} identifier.");
            }
        }

        return map;
    }

    private static Dictionary<string, int> CreateOutputMap(
        ImmutableArray<SourceFormulaDefinition> definitions)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < definitions.Length; index++)
        {
            SourceFormulaDefinition? definition = definitions[index];
            if (definition is null
                || !GlobalParameterId.IsValid(definition.OutputId)
                || !map.TryAdd(definition.OutputId, index))
            {
                throw Error(
                    SourceFormulaErrorCode.DuplicateIdentifier,
                    "Invalid or duplicate output identifier.");
            }
        }

        return map;
    }

    private static void ValidateDefinition(SourceFormulaDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Expression)
            || definition.Expression.Length > MaximumExpressionLength
            || !double.IsFinite(definition.NeutralValue)
            || !double.IsFinite(definition.SuggestedMinimum)
            || !double.IsFinite(definition.SuggestedMaximum)
            || definition.SuggestedMinimum > definition.SuggestedMaximum
            || definition.NeutralValue < definition.SuggestedMinimum
            || definition.NeutralValue > definition.SuggestedMaximum)
        {
            throw Error(SourceFormulaErrorCode.InvalidDefinition, "Invalid formula definition.");
        }
    }

    private static IEnumerable<VariableNode> EnumerateReferences(ExpressionNode node)
    {
        switch (node)
        {
            case VariableNode variable:
                yield return variable;
                break;
            case UnaryNode unary:
                foreach (VariableNode reference in EnumerateReferences(unary.Operand))
                {
                    yield return reference;
                }

                break;
            case BinaryNode binary:
                foreach (VariableNode reference in EnumerateReferences(binary.Left))
                {
                    yield return reference;
                }

                foreach (VariableNode reference in EnumerateReferences(binary.Right))
                {
                    yield return reference;
                }

                break;
            case FunctionNode function:
                foreach (ExpressionNode argument in function.Arguments)
                {
                    foreach (VariableNode reference in EnumerateReferences(argument))
                    {
                        yield return reference;
                    }
                }

                break;
        }
    }

    private static ImmutableArray<int> CreateEvaluationOrder(HashSet<int>[] dependencies)
    {
        var states = new byte[dependencies.Length];
        var order = ImmutableArray.CreateBuilder<int>(dependencies.Length);
        for (int index = 0; index < dependencies.Length; index++)
        {
            Visit(index, dependencies, states, order);
        }

        return order.ToImmutable();
    }

    private static bool ParticipatesInCycle(
        int start,
        int current,
        HashSet<int>[] dependencies,
        HashSet<int> visited)
    {
        foreach (int dependency in dependencies[current])
        {
            if (dependency == start)
            {
                return true;
            }

            if (visited.Add(dependency)
                && ParticipatesInCycle(start, dependency, dependencies, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static void Visit(
        int index,
        HashSet<int>[] dependencies,
        byte[] states,
        ImmutableArray<int>.Builder order)
    {
        if (states[index] == 2)
        {
            return;
        }

        if (states[index] == 1)
        {
            throw Error(SourceFormulaErrorCode.CyclicDependency, "Formula dependency cycle detected.");
        }

        states[index] = 1;
        foreach (int dependency in dependencies[index].Order())
        {
            Visit(dependency, dependencies, states, order);
        }

        states[index] = 2;
        order.Add(index);
    }

    private static void Emit(
        ExpressionNode node,
        IReadOnlyDictionary<string, int> inputs,
        IReadOnlyDictionary<string, int> outputs,
        ImmutableArray<FormulaInstruction>.Builder instructions)
    {
        switch (node)
        {
            case ConstantNode constant:
                instructions.Add(new FormulaInstruction(
                    FormulaOperation.Constant,
                    Constant: constant.Value));
                return;
            case VariableNode variable:
                instructions.Add(inputs.TryGetValue(variable.Id, out int inputSlot)
                    ? new FormulaInstruction(FormulaOperation.Input, inputSlot)
                    : new FormulaInstruction(FormulaOperation.Output, outputs[variable.Id]));
                return;
            case UnaryNode unary:
                Emit(unary.Operand, inputs, outputs, instructions);
                instructions.Add(new FormulaInstruction(FormulaOperation.Negate));
                return;
            case BinaryNode binary:
                Emit(binary.Left, inputs, outputs, instructions);
                Emit(binary.Right, inputs, outputs, instructions);
                instructions.Add(new FormulaInstruction(binary.Operator switch
                {
                    TokenKind.Plus => FormulaOperation.Add,
                    TokenKind.Minus => FormulaOperation.Subtract,
                    TokenKind.Star => FormulaOperation.Multiply,
                    TokenKind.Slash => FormulaOperation.Divide,
                    _ => throw new InvalidOperationException("Unknown binary operator."),
                }));
                return;
            case FunctionNode function:
                EmitFunction(function, inputs, outputs, instructions);
                return;
            default:
                throw new InvalidOperationException("Unknown formula node.");
        }
    }

    private static void EmitFunction(
        FunctionNode function,
        IReadOnlyDictionary<string, int> inputs,
        IReadOnlyDictionary<string, int> outputs,
        ImmutableArray<FormulaInstruction>.Builder instructions)
    {
        if (!SourceFormulaLanguage.TryGetFunction(function.Name, out FormulaFunctionDefinition? definition))
        {
            throw Error(
                SourceFormulaErrorCode.UnsupportedFunction,
                $"Unsupported formula function: {function.Name}",
                function.Start,
                function.Length);
        }

        FormulaOperation operation = function.Name switch
        {
            "abs" => FormulaOperation.Absolute,
            "min" => FormulaOperation.Minimum,
            "max" => FormulaOperation.Maximum,
            "clamp" => FormulaOperation.Clamp,
            "degToRad" => FormulaOperation.DegreesToRadians,
            _ => throw new InvalidOperationException("Formula catalog and compiler operations diverged."),
        };
        if (function.Arguments.Length != definition.Arity)
        {
            throw Error(
                SourceFormulaErrorCode.Syntax,
                "Formula function arity is invalid.",
                function.Start,
                function.Length);
        }

        foreach (ExpressionNode argument in function.Arguments)
        {
            Emit(argument, inputs, outputs, instructions);
        }

        instructions.Add(new FormulaInstruction(operation));
    }

    private static int CalculateMaximumStackDepth(
        ImmutableArray<FormulaInstruction>.Builder instructions)
    {
        int depth = 0;
        int maximum = 0;
        foreach (FormulaInstruction instruction in instructions)
        {
            depth += instruction.Operation switch
            {
                FormulaOperation.Constant or FormulaOperation.Input or FormulaOperation.Output => 1,
                FormulaOperation.Add or FormulaOperation.Subtract or FormulaOperation.Multiply
                    or FormulaOperation.Divide or FormulaOperation.Minimum or FormulaOperation.Maximum => -1,
                FormulaOperation.Clamp => -2,
                _ => 0,
            };
            maximum = Math.Max(maximum, depth);
            if (depth < 1 || maximum > MaximumStackDepth)
            {
                throw Error(SourceFormulaErrorCode.ComplexityLimit, "Formula stack limit exceeded.");
            }
        }

        if (depth != 1)
        {
            throw Error(SourceFormulaErrorCode.Syntax, "Formula did not produce one value.");
        }

        return maximum;
    }

    private static SourceFormulaCompilationException Error(
        SourceFormulaErrorCode code,
        string message,
        int start = 0,
        int length = 0) => new(code, message, start, length);

    private abstract record ExpressionNode;

    private sealed record ConstantNode(double Value) : ExpressionNode;

    private sealed record VariableNode(string Id, int Start, int Length) : ExpressionNode;

    private sealed record UnaryNode(ExpressionNode Operand) : ExpressionNode;

    private sealed record BinaryNode(
        TokenKind Operator,
        ExpressionNode Left,
        ExpressionNode Right) : ExpressionNode;

    private sealed record FunctionNode(
        string Name,
        ImmutableArray<ExpressionNode> Arguments,
        int Start,
        int Length) : ExpressionNode;

    private enum TokenKind
    {
        End,
        Number,
        Identifier,
        Plus,
        Minus,
        Star,
        Slash,
        LeftParenthesis,
        RightParenthesis,
        Comma,
    }

    private readonly record struct Token(
        TokenKind Kind,
        string Text,
        int Start,
        int Length,
        double Number = 0);

    private sealed class Parser
    {
        private readonly string expression;
        private int position;
        private int nestingDepth;
        private Token current;

        internal Parser(string expression)
        {
            this.expression = expression;
            current = ReadToken();
        }

        internal ExpressionNode Parse()
        {
            ExpressionNode node = ParseAdditive();
            if (current.Kind != TokenKind.End)
            {
                throw Error(SourceFormulaErrorCode.Syntax, "Unexpected formula token.");
            }

            return node;
        }

        private ExpressionNode ParseAdditive()
        {
            ExpressionNode left = ParseMultiplicative();
            while (current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                TokenKind operation = current.Kind;
                Advance();
                left = new BinaryNode(operation, left, ParseMultiplicative());
            }

            return left;
        }

        private ExpressionNode ParseMultiplicative()
        {
            ExpressionNode left = ParseUnary();
            while (current.Kind is TokenKind.Star or TokenKind.Slash)
            {
                TokenKind operation = current.Kind;
                Advance();
                left = new BinaryNode(operation, left, ParseUnary());
            }

            return left;
        }

        private ExpressionNode ParseUnary()
        {
            if (current.Kind == TokenKind.Minus)
            {
                Advance();
                return new UnaryNode(ParseUnary());
            }

            if (current.Kind == TokenKind.Plus)
            {
                Advance();
                return ParseUnary();
            }

            return ParsePrimary();
        }

        private ExpressionNode ParsePrimary()
        {
            if (current.Kind == TokenKind.Number)
            {
                double value = current.Number;
                Advance();
                return new ConstantNode(value);
            }

            if (current.Kind == TokenKind.Identifier)
            {
                Token identifier = current;
                Advance();
                return current.Kind == TokenKind.LeftParenthesis
                    ? ParseFunction(identifier)
                    : new VariableNode(identifier.Text, identifier.Start, identifier.Length);
            }

            if (current.Kind == TokenKind.LeftParenthesis)
            {
                EnterNesting();
                try
                {
                    Advance();
                    ExpressionNode node = ParseAdditive();
                    Require(TokenKind.RightParenthesis);
                    Advance();
                    return node;
                }
                finally
                {
                    nestingDepth--;
                }
            }

            throw Error(SourceFormulaErrorCode.Syntax, "Formula value expected.");
        }

        private FunctionNode ParseFunction(Token identifier)
        {
            EnterNesting();
            try
            {
                Advance();
                var arguments = ImmutableArray.CreateBuilder<ExpressionNode>();
                if (current.Kind != TokenKind.RightParenthesis)
                {
                    while (true)
                    {
                        arguments.Add(ParseAdditive());
                        if (current.Kind != TokenKind.Comma)
                        {
                            break;
                        }

                        Advance();
                    }
                }

                Require(TokenKind.RightParenthesis);
                Advance();
                return new FunctionNode(
                    identifier.Text,
                    arguments.ToImmutable(),
                    identifier.Start,
                    identifier.Length);
            }
            finally
            {
                nestingDepth--;
            }
        }

        private void EnterNesting()
        {
            nestingDepth++;
            if (nestingDepth > MaximumNestingDepth)
            {
                throw Error(SourceFormulaErrorCode.ComplexityLimit, "Formula nesting limit exceeded.");
            }
        }

        private void Require(TokenKind kind)
        {
            if (current.Kind != kind)
            {
                throw Error(SourceFormulaErrorCode.Syntax, "Formula token was missing.");
            }
        }

        private void Advance() => current = ReadToken();

        private Token ReadToken()
        {
            while (position < expression.Length && char.IsWhiteSpace(expression[position]))
            {
                position++;
            }

            if (position >= expression.Length)
            {
                return new Token(TokenKind.End, string.Empty, position, 0);
            }

            int start = position;
            char value = expression[position];
            position++;
            return value switch
            {
                '+' => new Token(TokenKind.Plus, "+", start, 1),
                '-' => new Token(TokenKind.Minus, "-", start, 1),
                '*' => new Token(TokenKind.Star, "*", start, 1),
                '/' => new Token(TokenKind.Slash, "/", start, 1),
                '(' => new Token(TokenKind.LeftParenthesis, "(", start, 1),
                ')' => new Token(TokenKind.RightParenthesis, ")", start, 1),
                ',' => new Token(TokenKind.Comma, ",", start, 1),
                _ when char.IsDigit(value) || value == '.' => ReadNumber(start),
                _ when char.IsLetter(value) || value == '_' => ReadIdentifier(start),
                _ => throw Error(
                    SourceFormulaErrorCode.Syntax,
                    "Formula contains an unsupported character.",
                    start,
                    1),
            };
        }

        private Token ReadNumber(int start)
        {
            while (position < expression.Length
                && (char.IsDigit(expression[position])
                    || expression[position] is '.' or 'e' or 'E' or '+' or '-'))
            {
                char candidate = expression[position];
                if (candidate is '+' or '-'
                    && expression[position - 1] is not ('e' or 'E'))
                {
                    break;
                }

                position++;
            }

            string text = expression[start..position];
            if (!double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsed)
                || !double.IsFinite(parsed))
            {
                throw Error(SourceFormulaErrorCode.Syntax, "Formula number is invalid.");
            }

            return new Token(TokenKind.Number, text, start, position - start, parsed);
        }

        private Token ReadIdentifier(int start)
        {
            while (position < expression.Length
                && (char.IsLetterOrDigit(expression[position])
                    || expression[position] is '_' or '.'))
            {
                position++;
            }

            string text = expression[start..position];
            if (text[^1] == '.'
                || text.Contains("..", StringComparison.Ordinal))
            {
                throw Error(SourceFormulaErrorCode.Syntax, "Formula identifier is invalid.");
            }

            return new Token(TokenKind.Identifier, text, start, position - start);
        }
    }
}
