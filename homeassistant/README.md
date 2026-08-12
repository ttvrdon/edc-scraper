# Home Assistant / HACS package

This folder contains the Home Assistant custom integration package, separated from the C# library code.

## Structure

- [hacs.json](C:/Projects/Personal/edc-scraper.worktrees/update-energydatarecord-calculations/homeassistant/hacs.json)
- [custom_components/edc_sharing/](C:/Projects/Personal/edc-scraper.worktrees/update-energydatarecord-calculations/homeassistant/custom_components/edc_sharing/)

## What is already done

- Integration code and config flow
- English translation
- Czech translation: [translations/cs.json](C:/Projects/Personal/edc-scraper.worktrees/update-energydatarecord-calculations/homeassistant/custom_components/edc_sharing/translations/cs.json)
- Portal web-icon (tab style) asset: [icon.svg](C:/Projects/Personal/edc-scraper.worktrees/update-energydatarecord-calculations/homeassistant/custom_components/edc_sharing/icon.svg)

## To make it available in HACS

HACS expects the repository root to contain `hacs.json` and `custom_components/<domain>`.
Because this mono-repo also contains C# code, you should publish the HA package as a dedicated repository:

1. Create a new GitHub repository (e.g. `edc-sharing-ha`).
2. Copy the content of this [homeassistant/](C:/Projects/Personal/edc-scraper.worktrees/update-energydatarecord-calculations/homeassistant/) folder to that repository root.
3. Tag a release (e.g. `v0.1.0`).
4. Validate with `hacs/action` (optional but recommended).
5. Add the repository URL as a custom integration repository in HACS.

## To show the icon in Home Assistant UI

For custom integrations, Home Assistant gets the domain icon from `home-assistant/brands`:

1. Keep [icon.svg](C:/Projects/Personal/edc-scraper.worktrees/update-energydatarecord-calculations/homeassistant/custom_components/edc_sharing/icon.svg) in this repo as source.
2. Open a PR to `home-assistant/brands` adding `custom_integrations/edc_sharing/icon.svg` (and logo if desired).
3. After merge, the icon is displayed automatically for the integration domain.
