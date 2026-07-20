# Linux symbol index

Generated public/protected declaration surfaces for the Linux/Avalonia tree:
**79 files / 3805 declaration lines**. Grep this directory before opening source:

```sh
rg -n "SubscribeTicksAsync" .claude/context/linux/symbols/
```

| Family | Files | Naming |
|---|---:|---|
| Core | 23 | `symbols/Core-<area>.md` |
| Infrastructure | 24 | `symbols/Infrastructure-<area>.md` |
| Other product projects | 32 | `symbols/<project>.md` |

Tests are intentionally omitted from the API surface. Multi-line signatures show their first line;
source-generated properties such as `[ObservableProperty]` are not visible to the extractor.
