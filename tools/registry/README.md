# Coder extension registry

Coder finds extensions in two places:

- **Open VSX** (open-vsx.org), the open registry of VS Code extensions. Coder offers the color themes
  there, because themes are data and Coder can use them as they are.
- **Coder's own registry**: https://github.com/HamzaBaghdadi/coder-extensions (the default `extensionRegistry`).
  Publishing is a pull request that adds an entry to its `index.json`; packages live in GitHub Releases.
  The full guide and the validation check are in that repository.

Coder never runs anything from an extension. A package is downloaded, checked against its SHA-256,
size-limited (64 MB), unpacked into a staging folder that no entry may escape, and moved into
`<user data>/extensions/<publisher.name-version>`.

## index.json

```json
{
  "extensions": [
    {
      "publisher": "acme",
      "name": "ember",
      "displayName": "Acme Ember",
      "description": "Warm dark colors for long evenings",
      "version": "1.0.0",
      "download": "https://github.com/acme/ember/releases/download/v1.0.0/acme.ember-1.0.0.vsix",
      "sha256": "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08",
      "icon": "https://raw.githubusercontent.com/acme/ember/main/icon.png",
      "downloads": 0,
      "verified": false
    }
  ]
}
```

- `download` may be relative to the index (a mirror can ship packages next to it).
- `sha256` is required in the public registry: Coder refuses a package that doesn't match.

## A package

The same layout as a VS Code extension, so one package works in both editors: a `.vsix` (a zip whose
`extension/` folder holds `package.json`), or a zip with `package.json` at its root.

```json
{
  "publisher": "acme",
  "name": "ember",
  "version": "1.0.0",
  "displayName": "Acme Ember",
  "icon": "icon.png",
  "contributes": {
    "themes": [{ "label": "Acme Ember", "uiTheme": "vs-dark", "path": "./themes/ember.json" }]
  }
}
```

Coder uses `contributes.themes` today. Next: language servers (a server command and the npm package
that provides it), snippets and icon themes, all declared in the same manifest.

To compute the checksum: `(Get-FileHash acme.ember-1.0.0.vsix -Algorithm SHA256).Hash`.
