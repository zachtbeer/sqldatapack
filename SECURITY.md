# Security Policy

## Maintainers

SqlDataPack is maintained by zachtbeer, who owns the `SqlDataPack` NuGet package.

## Reporting a Vulnerability

If you discover a security vulnerability, please report it responsibly:

1. **Do not open a public issue.**
2. Preferred: use [GitHub's private security advisory](https://github.com/zachtbeer/sqldatapack/security/advisories/new) to report the vulnerability.
3. Backup: email the maintainer security contact at `security@zachtbeerlabs.nl`.

## What to Expect

- Acknowledgment within 48 hours.
- A fix or mitigation plan within a reasonable timeframe depending on severity.
- Credit in the release notes unless you prefer to remain anonymous.

## Scope

Security fixes are considered for the latest stable major version.

This library connects to SQL Server databases using credentials you provide, writes local package files, and can optionally capture or deploy schema packages. It does not intentionally store, transmit, or log connection strings or credentials.

## Documentation Site Dependencies

The `website/` directory holds the documentation site and the npm toolchain that builds it. None of it ships in the `SqlDataPack` NuGet package, which contains no npm code. Those dependencies run at build time against this repository's own content, and what gets published is static HTML, CSS, and JavaScript.

Dependabot tracks that dependency tree weekly, alongside the NuGet and GitHub Actions trees.

Some advisories in it currently have no fix available from here: the site already runs the latest Docusaurus, and the affected packages are pinned inside it. Those advisories are tracked rather than enforced as a build gate, because a gate that no change in this repository can satisfy would block documentation deploys on another project's release schedule.

If you believe an advisory in the documentation toolchain is exploitable against this project, report it through the private advisory process above rather than opening a public issue.
