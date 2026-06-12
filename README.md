# QuickSwap

一键批量启动 / 关闭 Windows 应用。

- 自动扫描开始菜单，双击即可添加应用
- 勾选 → 点按钮 → 批量启动或温和关闭
- 关闭采用三步递进：WM_CLOSE → taskkill → /F 强杀

## 环境要求

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## 编译运行

```powershell
dotnet build
dotnet run
```

## 打包成独立 exe

生成单个 exe 文件，无需安装 .NET 即可在任意 Windows 上运行：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

exe 生成在：

```
bin\Release\net8.0-windows\win-x64\publish\QuickSwap.exe
```

把这个 exe 和自动生成的 `modes_config.json` 放在同一目录即可。
