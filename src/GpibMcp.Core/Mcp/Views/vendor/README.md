# Vendored: MCP Apps client

`ext-apps-1.7.5-app-with-deps.js` is `dist/src/app-with-deps.js` from the npm package
[`@modelcontextprotocol/ext-apps`](https://github.com/modelcontextprotocol/ext-apps) 1.7.5, unchanged.
Its licence is in `ext-apps-LICENSE.txt` (MIT, moving to Apache-2.0).

It is built into `GpibMcp.Core` as an embedded resource and inlined into the screen view (#157), so the
view works on a bench PC with no internet access. Fetching it from a CDN at runtime would not.

- Source: `https://unpkg.com/@modelcontextprotocol/ext-apps@1.7.5/dist/src/app-with-deps.js`
- SHA-256: `5BC0452B9994217DF506CB505AF20E97E766700513AFB7D0B45D255785BB051F`

To update it, download the new version's file, replace this one (renaming it to the new version), update
the resource name in `GpibMcp.Core.csproj` and `McpAppViews.cs`, and run the tests: `McpAppViewsTests`
checks that the bundle still exports `App` the way the view expects.
