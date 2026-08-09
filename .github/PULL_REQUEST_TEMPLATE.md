## Summary

Describe the change and why it was made.

## Changes

-
-
-

## Validation

- [ ] `pre-commit run --all-files`
- [ ] `dotnet restore && dotnet build --configuration Release && dotnet test --configuration Release`
- [ ] CI workflows pass

## Review checklist

- [ ] Code changes are scoped and minimal
- [ ] No secrets or local build artifacts were added
- [ ] Dependabot/CodeQL config is correct
- [ ] `SECURITY.md` and `CONTRIBUTING.md` are present
- [ ] `.gitignore` excludes local artifacts
