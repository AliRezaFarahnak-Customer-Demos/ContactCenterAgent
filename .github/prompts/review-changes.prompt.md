# Review Unpushed Changes

Go through all unpushed code changes in detail. Cross-check against Microsoft Docs and reference GitHub samples to ensure everything is correct — this code will ship to millions of users, and even a small bug could cost millions.

## Steps

1. **Diff review**: Walk through every unpushed change file by file.
2. **Validate against docs**: For every Azure SDK call, API surface, or config setting touched, confirm the usage matches the latest official documentation and sample code.
3. **Edge cases & failure modes**: Check error handling, retries, timeouts, and security implications (OWASP Top 10).
4. **Update documentation**: Refresh [copilot-instructions.md](../copilot-instructions.md) and any other affected `.md` files. Replace legacy or stale knowledge with what's now true in the codebase.
