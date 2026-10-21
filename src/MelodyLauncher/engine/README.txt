构建用内核二进制说明
====================

本仓库不包含内核可执行文件（闭源组件，随发布包分发）。

开发/构建时，请把内核放到：

    src/MelodyLauncher/engine/core.exe

然后执行：

    dotnet build src/MelodyLauncher/MelodyLauncher.csproj -c Release
    dotnet publish src/MelodyLauncher/MelodyLauncher.csproj -c Release

发布版（release 目录下的 MCLauncher.exe）是自包含单文件，已内置内核，无需额外文件。
