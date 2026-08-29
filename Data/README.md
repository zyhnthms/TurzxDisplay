# Data — 运行数据

## china-cities.tsv

中国城市/区县表（约 3600 行，`Adm1\tAdm2\t名称\t纬度\t经度`），供天气模式的
**和风天气位置搜索**与**自动定位细化到区县**使用；缺失时应用可正常运行，
只是位置搜索不可用。

由于源数据 [qwd/LocationList](https://github.com/qwd/LocationList) 的
China-City-List **未附带开源许可证**（默认保留所有权利），本仓库不直接分发
其衍生数据文件。请自行下载并转换：

```bash
node tools/update-city-list.mjs
```

脚本会从 GitHub 拉取最新 CSV，转换并写入 `Data/china-cities.tsv`
（随后由 csproj 的 Content 项复制到构建输出）。
