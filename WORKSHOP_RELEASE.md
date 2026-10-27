# Steam 创意工坊发布规则

## 描述只保留最新版

`workshop/Sts2Companion/workshop.json` 的 `description` 只写**当前版本**的更新日志，不累积旧版本。

每次发版：

1. 把 `description` 替换成当前版本的更新内容
2. `changeNote` 写同一版本的一句话摘要
3. 保留 `title`、`tags`、`dependencies`
4. 不上传本地 `companion_ai.json` / API Key / state 目录
5. 更新公共版后，把 DLL/JSON 同步到本机 Steam 创意工坊缓存 `3827739973`

## 当前版本

```text
v0.4.1
```

## 私有测试版

- 私人测试条目：`3828157486`
- 测试版可以带 `TEST BUILD` 日志和临时实现
- 测试通过后再合并到公共版 `3827739973`
