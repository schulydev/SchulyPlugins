# SchulyPlugins

The plugins that connect [SchulyServer](https://github.com/schulydev/SchulyServer) to school systems and Microsoft 365. A server downloads the ones in its `plugins.yml` when it starts.

| Plugin | Connects to |
|---|---|
| [`Schuly.Plugin.Schulware`](src/Schuly.Plugin.Schulware) | Schulnetz, through [SchulwareAPI](https://github.com/PianoNic/SchulwareAPI) |
| [`Schuly.Plugin.OdaOrg`](src/Schuly.Plugin.OdaOrg) | OdA Org, the ICT-BBAG apprenticeship portal |
| [`Schuly.Plugin.Microsoft`](src/Schuly.Plugin.Microsoft) | A student's Microsoft 365, through [SchulyMicrosoft](https://github.com/schulydev/SchulyMicrosoft) |
| [`Schuly.Plugin.Example`](src/Schuly.Plugin.Example) | Nothing. Copy this one to start your own. |

Your school isn't there? [Add a plugin](https://docs.schuly.dev/SchulyPlugins/adding-a-plugin) takes you from a copy of the example to a plugin your local server loads. The rest is in [the plugin docs](https://docs.schuly.dev/SchulyPlugins/).

[MIT](LICENSE)
