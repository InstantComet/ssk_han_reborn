# Sunless Skies 汉化安装器

## 使用说明

1. 双击运行 `SskCn汉化安装器.exe`
2. 程序会自动检测常见的 Steam 安装位置
3. 如果未检测到，点击"浏览..."手动选择游戏目录（包含 `Sunless Skies.exe` 的文件夹）
4. 点击"开始安装"
5. 等待安装完成

## 前置要求

- **BepInEx 6.0 (IL2CPP 版)**：必须先安装 BepInEx 才能使用汉化补丁
  - 下载地址：https://github.com/BepInEx/BepInEx/releases
  - 选择 `BepInEx-Unity.IL2CPP-win-x64-xxx.zip`

## 安装的文件

安装程序会将以下文件放置到 `<游戏目录>/BepInEx/plugins/` 下：

```
BepInEx/
└── plugins/
    ├── SskCnPoc.dll          # 汉化插件主程序
    ├── para/                  # 翻译文件
    │   ├── areas.json
    │   ├── events.json
    │   ├── qualities.json
    │   └── ...
    └── Fonts/
        └── sourcehan          # 中文字体资源
```

## 卸载

删除 `BepInEx/plugins/` 下的以下文件/文件夹即可：
- `SskCnPoc.dll`
- `para/` 文件夹
- `Fonts/` 文件夹

## 构建安装程序

如果需要重新构建安装程序：

```powershell
cd Installer
.\build-installer.ps1
```

这会：
1. 构建 SskCnPoc 插件
2. 收集所有资源文件
3. 生成单文件 exe 安装程序

输出位置：`Installer/Output/SskCn汉化安装器.exe`
