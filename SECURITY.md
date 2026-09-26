# Security

## Reporting a problem

Please report security problems privately: on GitHub, open this repository's **Security** tab and
choose **Report a vulnerability**. Please don't open a public issue for them. You'll get a reply
within a week.

## What Study Stash protects, and how

- **The library** is protected by one password, sent over your own network or Tailscale. It's
  plain http, so keep it on Tailscale or a network you trust; don't expose its port to the
  internet.
- **Claude's own door** (for claude.ai, over Tailscale Funnel) is a separate OAuth 2.1
  authorization server: authorization code with PKCE, short-lived access tokens with rotating
  refresh tokens, and dynamic client registration. Only token hashes are kept, and repeated wrong
  sign-in attempts are slowed down. A token made by hand in Settings, for another MCP client, can
  be revoked on its own.
- **Secrets stay on your own computer.** The library's config and its Claude access file are
  written readable by you only.
- **Updates** come only from this repository's GitHub releases, which CI builds from `main` after
  the tests pass. Anyone who controls the repository controls the updates, so the maintainer's
  GitHub account uses two-factor authentication. To turn off automatic updates, set
  `auto_update = false` in the config.

## Supported versions

Only the newest release gets fixes. `studystash update` installs it.
