# Security

## Reporting a problem

Please report security problems privately: on GitHub, open this repository's **Security** tab and
choose **Report a vulnerability**. Please don't open a public issue for them. You'll get a reply
within a week.

## What Study Stash protects, and how

- **The laptop's page** listens on 127.0.0.1 only. It checks the Host header against DNS
  rebinding, needs a per-install token, and requires a custom header on every change, which web
  pages on other sites can't send.
- **The library** is protected by one password, sent over your own network or Tailscale. It's
  plain http, so keep it on Tailscale or a network you trust. Don't expose port 8787 to the
  internet.
- **Secrets stay on your computers.** Granola's sign-in tokens (`tokens.json`) and the config files
  are written readable by you only.
- **Updates** come only from this repository's GitHub releases, which CI builds from `main` after
  the tests pass. Anyone who controls the repository controls the updates, so the maintainer's
  GitHub account uses two-factor authentication. To turn off automatic updates, set
  `auto_update = false` in the config.

## Supported versions

Only the newest release gets fixes. `granola-share update` installs it.
