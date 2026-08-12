using System.Collections.Immutable;

namespace Motara.App.Shell;

public sealed record MenuColumn
{
    public MenuColumn(string id, string titleResourceKey, IEnumerable<MenuNode> nodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleResourceKey);
        ArgumentNullException.ThrowIfNull(nodes);
        ImmutableArray<MenuNode> materialized = nodes.ToImmutableArray();
        if (materialized.IsEmpty)
        {
            throw new ArgumentException("A menu column requires at least one node.", nameof(nodes));
        }

        Id = id;
        TitleResourceKey = titleResourceKey;
        Nodes = materialized;
    }

    public string Id { get; }

    public string TitleResourceKey { get; }

    public ImmutableArray<MenuNode> Nodes { get; }
}

public sealed record MenuLevelGroup
{
    public MenuLevelGroup(string id, IEnumerable<MenuColumn> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(columns);
        ImmutableArray<MenuColumn> materialized = columns.ToImmutableArray();
        if (materialized.IsEmpty)
        {
            throw new ArgumentException("A menu level group requires at least one column.", nameof(columns));
        }

        if (materialized.Select(column => column.Id).Distinct(StringComparer.Ordinal).Count()
            != materialized.Length)
        {
            throw new ArgumentException("Menu column IDs must be unique.", nameof(columns));
        }

        Id = id;
        Columns = materialized;
    }

    public string Id { get; }

    public ImmutableArray<MenuColumn> Columns { get; }

    public static MenuLevelGroup SingleColumn(
        string id,
        string titleResourceKey,
        IEnumerable<MenuNode> nodes) =>
        new(id, [new MenuColumn($"{id}.column", titleResourceKey, nodes)]);
}
