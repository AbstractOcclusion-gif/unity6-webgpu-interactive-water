# Repository Git hooks

Activate the tracked hooks once per clone:

```powershell
git config core.hooksPath .githooks
```

The pre-commit hook rejects staged Unity source files whose working copies contain CRLF or mixed
line endings. `.gitattributes` defines LF as the repository format for those files. The hook uses
Windows PowerShell or PowerShell 7 and fails closed when neither runtime is available.
