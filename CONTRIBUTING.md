# Contributing to DeskHush

DeskHush accepts focused bug fixes, tests, documentation improvements, and features that fit its local, reversible Windows-management scope.

## Development setup

Requirements:

- Windows 10 or Windows 11;
- .NET 8 SDK;
- Git;
- Visual Studio 2022 is optional.

```powershell
git clone https://github.com/sanrokamlan-prog/DeskHush.git
cd DeskHush
dotnet restore DeskHush.sln
dotnet build DeskHush.sln -c Release --no-restore
dotnet run --project tests/DeskHush.Tests/DeskHush.Tests.csproj -c Release
```

Run the desktop application with:

```powershell
dotnet run --project src/DeskHush.App/DeskHush.App.csproj
```

The enumeration smoke tests read the local registry and Startup folders but must not modify them. Use a disposable Windows VM for tests that exercise write and recovery paths.

## Project boundaries

- `DeskHush.Core` contains platform-neutral models, interfaces, validation, matching, and JSON settings behavior.
- `DeskHush.Windows` contains Win32, registry, and Startup-folder adapters.
- `DeskHush.App` contains WPF presentation, ViewModels, tray behavior, and application lifecycle.
- `DeskHush.Tests` is a lightweight console test runner without a third-party test framework.

Keep Core free of WPF and direct Windows registry calls. Put reusable behavior behind an interface when it materially improves testability; avoid abstractions that only rename a single API call.

## Safety requirements

Changes that mutate Windows state must:

1. identify the exact scope, registry view, source path, and required privilege;
2. persist enough original state for exact recovery before mutation;
3. verify the result after writing, deleting, or moving;
4. preserve recovery data if verification fails;
5. refuse to overwrite a conflicting value, file, or directory;
6. avoid automatic Explorer termination, process injection, driver installation, and silent elevation;
7. include tests for pure logic and a documented VM test plan for Windows writes.

New startup sources such as scheduled tasks or services require their own typed model, enumeration boundary, privilege handling, backup format, conflict policy, and recovery tests. Do not expose a source in the UI until its write and restore paths are complete.

## Pull requests

Keep each pull request centered on one behavior. Before opening it:

```powershell
dotnet build DeskHush.sln -c Release
dotnet run --project tests/DeskHush.Tests/DeskHush.Tests.csproj -c Release
```

In the description, include the problem, the chosen behavior, affected Windows surfaces, validation performed, recovery behavior, and screenshots for UI changes. Call out any action that requires elevation or an Explorer/sign-in refresh.

Do not commit generated `bin`, `obj`, publish output, user state files, registry exports, credentials, private paths, or screenshots containing personal information.

## Security reports

Do not use a public pull request or issue for an exploitable vulnerability. Follow [SECURITY.md](SECURITY.md).
