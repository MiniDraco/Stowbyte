# Releases

The ready-to-run **Stowbyte.exe** is published on the GitHub Releases page, not stored in this
folder — the self-contained build is ~154 MB, which is over GitHub's 100 MB per-file limit for
the repo itself.

➡️ **[Download the latest Stowbyte.exe](https://github.com/MiniDraco/Stowbyte/releases/latest)**

It's a self-contained single file: download, double-click, done. No .NET install required.
Stowbyte self-elevates via UAC on launch because creating directory junctions into
`C:\Program Files` needs admin rights.

## Building it yourself

```
dotnet publish Loadout.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output lands in `publish/Stowbyte.exe`.
