# PurismCore Compliance Record

[English](PurismCore-Compliance.md) | [简体中文](PurismCore-Compliance.zh-CN.md)

## Purpose and Scope

This document records an internal technical review of Motara's use of
PurismCore. It describes the comparison that was performed and its limits. It
is not an opinion or conclusion about infringement, non-infringement,
liability, or immunity for any person or organization, and it is not legal
advice.

## Recorded Technical Review

- Scope: 52 Purism source or header files and 186 source or header files from
  Live2D's public Framework repository.
- Method: normalized code fragments were checked for exact equality.
- Recorded result: no exactly identical normalized fragment of 12 or more
  consecutive lines was found.

That result is limited to a check for verbatim or format-equivalent copying.
It does not by itself establish the absence of non-literal similarity, and it
does not replace analysis of source, development process, overall structure,
algorithmic flow, interface design, evidence, or applicable law.

## Compatibility and Public-Case Material

This repository includes a source-linked record of the Supreme People's Court
Gazette case *Beijing Jingdiao Technology Co., Ltd. v. Shanghai Naikai
Electronic Technology Co., Ltd.* (commonly identified as Supreme People's
Court Guiding Case No. 48):

- [Chinese source record](Guiding-Case-48.zh-CN.md)
- [Unofficial English translation](Guiding-Case-48.md)
- [Supreme People's Court Gazette source](http://gongbao.court.gov.cn/details/66c50dd1637ed679bebd9280a3d5b6.html)

The reasons in that case address output data files and their formats, the
purpose of technical measures, and software developed to read a particular
format for compatibility. The original Gazette source and the facts of a
particular dispute control its meaning and scope. Motara does not assert that
the case automatically resolves any other dispute.

## Limits of Any Indirect-Liability Analysis

In general legal analysis, aiding or indirect liability depends on the
governing jurisdiction, specific conduct, mental state, and any underlying
infringement, among other facts. Independent development, reverse analysis,
compliance with open-source licenses, and application-layer use may be
relevant facts, but this document cannot determine their legal effect in
advance. This repository makes no assertion that Live2D Inc., any PurismCore
developer, or any Motara developer is involved in an actual dispute,
infringement, or liability.

## Maintenance

If comparison material is later published or the scope changes, this document
must be updated with the file counts, method, date, and limitations. Specific
legal language should be reviewed by appropriately qualified counsel before
publication.
