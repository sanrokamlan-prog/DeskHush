# Security Policy

## Supported versions

Security fixes are applied to the latest published DeskHush release. Pre-release builds and older tags may be used for diagnosis, but should not be treated as supported security baselines.

| Version | Supported |
| --- | --- |
| Latest release | Yes |
| Older releases | No |

## Reporting a vulnerability

Please use GitHub's private vulnerability-reporting / **Security Advisories** flow for this repository. If GitHub does not show a private-report button, open a public issue containing only a request for a private contact channel; do not include exploit or environment details. Do not publish a vulnerability that could cause registry corruption, arbitrary file movement, privilege-boundary bypass, unintended window control, or disclosure of sensitive local paths.

Include:

- affected DeskHush version and Windows version;
- whether DeskHush was running normally or elevated;
- exact feature and entry type involved;
- minimal reproduction steps and observed result;
- logs, screenshots, or state-file excerpts with usernames and private paths redacted;
- your assessment of impact and any known workaround.

Please do not include credentials, personal files, or full registry exports. Acknowledgement and remediation are handled on a best-effort basis; this community project does not promise a fixed response SLA.

## Security boundaries

DeskHush is designed around the following boundaries:

- it runs as the current user by default and requests an explicit elevated restart for machine-wide changes;
- popup handling uses an out-of-context WinEvent hook and does not inject code into target processes;
- reversible registry and Startup-folder operations persist recovery state before mutation;
- restore operations stop on conflicts instead of overwriting a newly created value or file;
- it does not automatically terminate or restart Explorer;
- the current implementation has no telemetry, cloud sync, remote rule download, or automatic update channel.

DeskHush is not a security boundary, antivirus product, sandbox, or malware-removal tool. A process already running with the same or higher privileges may modify DeskHush's files, state, or registry entries.

## Release authenticity

Public release binaries are currently unsigned and may trigger Windows SmartScreen. Download only from this repository's Releases page and compare the archive against the published `SHA256SUMS`. A checksum confirms file identity, not publisher identity or absence of vulnerabilities.
