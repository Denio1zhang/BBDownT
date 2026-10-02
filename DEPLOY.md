# 部署网页版到服务器

BBDownT 的服务器模式（`serve`）自带网页前端。部署后用浏览器（电脑或手机）打开服务器地址，就可以提交下载、查看进度、扫码登录B站，并把下载好的文件下载到本机或在线播放。

## 一键部署（Docker）

服务器需要先装好 Docker（Linux 可用 `curl -fsSL https://get.docker.com | sh`）。

```bash
git clone https://github.com/denio1zhang/BBDownT.git
cd BBDownT
./deploy.sh
```

脚本会：

1. 生成 `.env`，里面有随机的访问令牌 `BBDOWNT_API_TOKEN` 和端口 `BBDOWNT_PORT`（默认 23333）；
2. 构建镜像（内置 ffmpeg）并在后台启动，容器异常退出或服务器重启后会自动拉起；
3. 打印访问地址和访问令牌。

然后在云服务器的安全组/防火墙里放行对应端口，浏览器打开 `http://服务器IP:23333`，输入访问令牌即可使用。

## 目录说明

| 路径 | 内容 |
| ---- | ---- |
| `./downloads` | 下载好的视频、音频、字幕等文件 |
| `./data` | B站登录信息（`BBDownT.data`）、配置文件（`BBDownT.config`）等 |
| `.env` | 访问令牌和端口，请勿泄露 |

## 常用操作

```bash
docker compose logs -f              # 查看日志
docker compose restart              # 重启
git pull && docker compose up -d --build   # 更新到最新代码
docker compose down                 # 停止并删除容器（下载的文件和登录信息会保留）
```

修改端口或令牌：编辑 `.env` 后执行 `docker compose up -d`。

## B站登录

点击网页右上角「登录B站」，用哔哩哔哩 App 扫码即可。登录后才能下载 1080P 及以上画质和会员内容。登录信息保存在 `./data/BBDownT.data`。

## 配置 HTTPS（可选，推荐）

访问令牌在 HTTP 下是明文传输的。如果有域名，建议在前面加一层反向代理来启用 HTTPS，例如使用 [Caddy](https://caddyserver.com/)：

```
bbdown.example.com {
    reverse_proxy 127.0.0.1:23333
}
```

此时可以把 `docker-compose.yml` 的端口映射改成 `"127.0.0.1:23333:23333"`，只允许通过反向代理访问。

## 不用 Docker

也可以直接运行独立二进制（需要自行安装 ffmpeg）：

```bash
export BBDOWNT_API_TOKEN=自己设置一个足够长的随机字符串
./BBDownT serve -l http://0.0.0.0:23333 --server-download-root ./downloads
```

可选环境变量 `BBDOWNT_DATA_DIR` 用于指定登录信息和配置文件的保存目录，默认是程序所在目录。
