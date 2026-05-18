# Third-Party Notices
MangosSuperUI uses the following open-source libraries. Their inclusion in this
GPL v2 licensed project is permitted under their respective licenses.
---
## Leaflet.js
Interactive map library used for the World Map page.
- **Copyright:** (c) 2010-2024, Volodymyr Agafonkin
- **License:** BSD 2-Clause
- **Website:** https://leafletjs.com/
- **Source:** https://github.com/Leaflet/Leaflet
- **Location in project:** `wwwroot/lib/leaflet/`
---
## Google model-viewer
Web component for displaying 3D GLB models on the Items and Game Objects pages.
- **Copyright:** (c) 2018 Google LLC
- **License:** Apache License 2.0
- **Website:** https://modelviewer.dev/
- **Source:** https://github.com/google/model-viewer
- **Location in project:** `wwwroot/lib/model-viewer.min.js`
---
## Font Awesome Free
Icon library used throughout the UI.
- **Copyright:** (c) 2024 Fonticons, Inc.
- **License:** Icons — CC BY 4.0 | Fonts — SIL OFL 1.1 | Code — MIT
- **Website:** https://fontawesome.com/
- **Source:** https://github.com/FortAwesome/Font-Awesome
- **Full license:** https://fontawesome.com/license/free
---
## jQuery
DOM manipulation and AJAX library used by all page JS files.
- **Copyright:** (c) OpenJS Foundation and contributors
- **License:** MIT
- **Website:** https://jquery.com/
- **Source:** https://github.com/jquery/jquery
---
## SignalR (Microsoft.AspNetCore.SignalR)
Real-time communication library used for the Console and Live Logs pages.
- **Copyright:** (c) .NET Foundation and Contributors
- **License:** MIT
- **Source:** https://github.com/dotnet/aspnetcore
---
## Dapper
Micro-ORM used for database access to VMaNGOS databases.
- **Copyright:** (c) Sam Saffron, Marc Gravell, Nick Craver
- **License:** Apache License 2.0
- **Source:** https://github.com/DapperLib/Dapper
---
## MySqlConnector
ADO.NET data provider for MySQL/MariaDB.
- **Copyright:** (c) Bradley Grainger
- **License:** MIT
- **Source:** https://github.com/mysql-net/MySqlConnector
---
## SkiaSharp
2D graphics library used for PNG encoding, texture processing, and BLP conversion in the Spell Creator pipeline.
- **Copyright:** (c) 2015-2024 The Mono Project
- **License:** MIT
- **Website:** https://github.com/mono/SkiaSharp
- **Source:** https://github.com/mono/SkiaSharp
- **Note:** `SkiaSharp.NativeAssets.Linux` provides the native libSkiaSharp.so for Linux deployment.
---
## War3Net.IO.Mpq
MPQ archive reader/writer used for reading WoW 1.12.1 client data (terrain, textures, models) and building custom patch MPQ archives.
- **Copyright:** (c) Drake53
- **License:** MIT
- **Source:** https://github.com/Drake53/War3Net
---
## War3Net.Drawing.Blp
BLP texture file decoder used for reading WoW BLP textures from MPQ archives and converting them to bitmaps for server-side texture compositing.
- **Copyright:** (c) Drake53
- **License:** MIT
- **Source:** https://github.com/Drake53/War3Net
---
## StormLib
Native MPQ archive library used via P/Invoke for reading/writing WoW 1.12.1 client MPQ archives. Used alongside War3Net.IO.Mpq for operations requiring the reference implementation (e.g., building custom patch MPQs with broad client compatibility).
- **Copyright:** (c) 1999-2024 Ladislav Zezula
- **License:** MIT
- **Website:** http://www.zezula.net/en/mpq/stormlib.html
- **Source:** https://github.com/ladislav-zezula/StormLib
- **Location in project:** `runtimes/linux-x64/native/libstorm.so`, `runtimes/win-x64/native/storm.dll`
---
## SharpGLTF
glTF 2.0 toolkit used for reading, writing, and manipulating GLB/glTF 3D models in the World Editor and Character Model Viewer pipelines.
- **Copyright:** (c) Vicente Penades
- **License:** MIT
- **Source:** https://github.com/vpenades/SharpGLTF
---
## Microsoft.Extensions.Hosting.Systemd
Systemd integration for ASP.NET Core — enables watchdog heartbeats and graceful SIGTERM handling when running as a Linux systemd service.
- **Copyright:** (c) .NET Foundation and Contributors
- **License:** MIT
- **Source:** https://github.com/dotnet/runtime
---
## Bootstrap
CSS framework used for layout and UI components.
- **Copyright:** (c) 2011-2024 The Bootstrap Authors
- **License:** MIT
- **Source:** https://github.com/twbs/bootstrap
