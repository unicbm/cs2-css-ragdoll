# cs2-css-ragdoll

CS2 死亡布娃娃插件。重枪能把人打飞，霰弹能打散肢体，倒地后还能继续射击。

Windows x64，需要 Metamod:Source（接口 18）、CounterStrikeSharp 1.0.378 和 .NET 10。
构建需要 Visual Studio 2026 C++、CMake、Python 3.10+、NumPy、Source2Viewer-CLI 和 CS2 Workshop Tools。将 `HL2SDKCS2` 和 `MMSOURCE_DEV` 分别指向 CS2 SDK 和含子模块的 Metamod 源码目录。

```powershell
.\build.ps1 -GameRoot "你的 Counter-Strike Global Offensive 目录"
```

将 `out/happy-wheel-ragdoll-win64.zip` 解压到 `game/csgo` 后重启游戏。联机时客户端也需要包内的模型和材质。

AGPL-3.0-only。
