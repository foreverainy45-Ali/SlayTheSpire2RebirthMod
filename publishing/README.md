# Rebirth / SeedRestart 发布工作区

状态：用户已确认 Rebirth 封面，v0.2.1 public-beta 修复已成功更新到同一私密测试条目。

条目 ID：`3840381989`。  
工坊链接：https://steamcommunity.com/sharedfiles/filedetails/?id=3840381989

- `workshop.json`：工坊标题、说明和更新记录；保持 `private`。
- `image.png`：用户审核通过的 Rebirth 主题封面，512×512，636575 字节。
- `content/`：实际上传的 Mod 文件。只包含发布构建和使用/验证说明。
- 工坊前置：JmcModLib（条目 `3747526103`）；上传器已返回 `Added dependency`。
- `_template-backup/`：初始模板备份，不在上传内容目录内。

对应源码项目：`I:\slayTheSpireBranch`。

后续更新时，在上传工具目录执行：

```powershell
.\ModUploader.exe upload -w "I:\ModUploader-win-x64\SeedRestartWorkshop"
```

0.2.1 public-beta 已上传成功，上传器退出码为 0，并返回 `Successfully uploaded`；发布 DLL SHA-256 为 `A2910D2BCB54A2009C49F5C8AFC649A9D74AF5AF1163D23AAF46279AA03C0813`。当前可见性保持 private，默认公开分支适配等待 beta 实机确认。

保留生成的 `mod_id.txt`，后续用同一工作区更新原条目，不要删除后再创建重复条目。
