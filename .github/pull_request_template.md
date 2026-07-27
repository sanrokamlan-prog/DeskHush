## Summary

Describe the problem and the behavior changed.

## Windows Surface

List affected registry locations, files, window events, scope, and required privilege.

## Safety And Recovery

Explain what is persisted before mutation, how results are verified, and how conflicts are handled.

## Validation

- [ ] `dotnet build DeskHush.sln -c Release -warnaserror`
- [ ] `dotnet run --project tests/DeskHush.Tests/DeskHush.Tests.csproj -c Release`
- [ ] Write paths were tested in a disposable Windows environment, or this change is read-only
- [ ] UI changes include redacted screenshots
