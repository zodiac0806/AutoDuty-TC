# -*- coding: utf-8 -*-
"""重算 AutoDuty/Resources/md5s.json（路徑檔派送清單）。

契約（改這支之前先讀）
--------------------
* 這份清單是**路徑檔唯一的派送管道**。路徑檔不隨外掛出貨，`Plugin.PathsDirectory`
  啟動時只是一個空目錄，內容全靠 `Updater/Patcher.PatchTask` 依這份清單下載。
* Patcher 的下載條件是「本機檔案 MD5 != 清單裡的 MD5」。⇒ 清單若停在**舊檔**的雜湊，
  使用者手上的舊檔跟它剛好相等 ⇒ 不下載，改動完全不生效，而且沒有任何錯誤訊息。
* 鍵是**檔名本身**（不含目錄），因為 Patcher 兩端都用 `fileInfo.Name`：本機清單以它
  當鍵，下載目的地也是 `PathsDirectory/{key}`。⇒ 兩個同名檔會讓 C# 端的 `ToDictionary`
  擲例外、整個 patch 被 catch 吞掉，所以這裡遇到同名就直接失敗。

🔴 雜湊的對象是 **git blob，不是工作樹檔案**
------------------------------------------
使用者下載到的位元組＝`raw.githubusercontent.com` 吐的 blob 內容，而
`DownloadFileAsync` 只是把收到的字串原樣寫檔、不動行尾。
本 repo 的 `.gitattributes` 有 `* text=auto`，Windows 開發機又是 `core.autocrlf=true`
⇒ **純 LF 的 blob 在 Windows 工作樹上是 CRLF**，兩者位元組不同、MD5 當然也不同。
拿工作樹算出來的雜湊寫進清單，在 Windows 上會一次改掉上百筆、而且每一筆都是錯的
（使用者每次 patch 都重新下載那些檔，永遠對不上）。
改用 `git cat-file` 讀 blob 之後，Windows 本機跑與 Linux runner 上跑結果相同。

格式（動了就會產生一顆假 diff）
------------------------------
UTF-8 無 BOM、LF、`indent=4`、`ensure_ascii=True`（日文檔名寫成 \\uXXXX）、
鍵**升冪排序**、值小寫十六進位、**檔尾沒有換行**。
上游 erdelf 的 `.github/workflows/md5filecreation.yml` 是
`md5sum AutoDuty/Paths/*` + `json.dump(..., indent=4)`，產出格式相同，
但本檔有兩處刻意不同：只收 `*.json`（本 fork 的 Paths 有 11 個非 .json 的編輯殘留
備份檔，上游那個 `*` glob 會把它們一起寫進清單），以及明確 `sorted()`
而不是靠 shell glob 的排序。

用法
----
    python .github/scripts/generate_md5s.py            # 重寫清單
    python .github/scripts/generate_md5s.py --check    # 只檢查，過期就 exit 1
"""
import hashlib
import json
import os
import subprocess
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
PATHS_PREFIX = "AutoDuty/Paths"
MD5_JSON = os.path.join(REPO, "AutoDuty", "Resources", "md5s.json")
TAB = chr(9)
NL = chr(10)


def git(*args, **kw):
    # 🔴 不帶 text=True：git 的輸出是 UTF-8，交給行程地區碼頁解會把日文檔名弄壞。
    return subprocess.run(["git", "-C", REPO] + list(args),
                          capture_output=True, check=True, **kw)


def render(mapping):
    """把 name -> md5 的對應表轉成落地用的位元組。"""
    return json.dumps(dict(sorted(mapping.items())), indent=4).encode("utf-8")


def calibrate():
    """兩道閘門：雜湊演算法本身，以及「現檔的格式假設」。

    第二道是重點：拿現檔自己的鍵值原樣重新 render，位元組必須同一。
    不同就代表格式假設已經跟現檔分岔，這時候重寫會產生一顆整份重排的假 diff。
    """
    if hashlib.md5(b"abc").hexdigest() != "900150983cd24fb0d6963f7d28e17f72":
        print("[calib] MD5 自我檢查失敗")
        return None
    if not os.path.exists(MD5_JSON):
        print("[calib] 現檔不存在，視為首次產生")
        return b""
    raw = open(MD5_JSON, "rb").read()
    try:
        existing = json.loads(raw.decode("utf-8"))
    except Exception as exc:
        print("[calib] 現檔不是合法 UTF-8 JSON：%s" % exc)
        return None
    if render(existing) != raw:
        print("[calib] 零修改往返不是位元組同一 —— 現檔的格式與本腳本的假設不符，")
        print("        直接重寫會產生一顆整份重排的假 diff。先確認格式再繼續。")
        return None
    print("[calib] MD5 演算法 OK；現檔零修改往返位元組同一（%d bytes）" % len(raw))
    return raw


def collect():
    """讀 index 裡 AutoDuty/Paths 底下每個 *.json 的 blob，回傳 檔名 -> md5。"""
    listing = git("-c", "core.quotepath=false", "ls-files", "-s", "--", PATHS_PREFIX)
    entries = []
    for line in listing.stdout.decode("utf-8").split(NL):
        if not line.strip():
            continue
        meta, path = line.split(TAB, 1)
        sha = meta.split()[1]
        if path.endswith(".json"):
            entries.append((sha, path))

    batch = git("cat-file", "--batch", input=NL.join(s for s, _ in entries).encode())
    out = batch.stdout
    mapping, origin, pos = {}, {}, 0
    for sha, path in entries:
        nl = out.index(b"\n", pos)
        size = int(out[pos:nl].decode().split()[2])
        blob = out[nl + 1:nl + 1 + size]
        pos = nl + 1 + size + 1
        name = path.rsplit("/", 1)[-1]
        if name in mapping:
            raise SystemExit("同名路徑檔：%s 與 %s —— C# 端 ToDictionary 會擲例外，"
                             "整個 patch 被 catch 吞掉。請先改名。" % (origin[name], path))
        origin[name] = path
        mapping[name] = hashlib.md5(blob).hexdigest()

    untracked = [p for p in git("ls-files", "--others", "--exclude-standard",
                                "--", PATHS_PREFIX).stdout.decode("utf-8").split(NL)
                 if p.endswith(".json")]
    if untracked:
        print("[warn] 未追蹤的路徑檔 %d 個，這一輪不會進清單（先 git add）：%s"
              % (len(untracked), untracked[:5]))
    return mapping


def main():
    check_only = "--check" in sys.argv[1:]
    old_raw = calibrate()
    if old_raw is None:
        return 2

    mapping = collect()
    blob = render(mapping)

    ctrl = [c for c in range(32) if c not in (9, 10, 13) and blob.count(bytes([c]))]
    if ctrl:
        print("[fail] 產出含控制字元：%s" % ctrl)
        return 2

    old = json.loads(old_raw.decode("utf-8")) if old_raw else {}
    added = sorted(set(mapping) - set(old))
    removed = sorted(set(old) - set(mapping))
    changed = sorted(k for k in mapping if k in old and old[k].lower() != mapping[k])
    print("路徑檔 %d 個｜清單原有 %d 筆" % (len(mapping), len(old)))
    print("  新增 %d %s" % (len(added), added[:8]))
    print("  移除 %d %s" % (len(removed), removed[:8]))
    print("  雜湊變更 %d %s" % (len(changed), changed[:8]))

    if blob == old_raw:
        print("清單已是最新，不動檔案。")
        return 0
    if check_only:
        print("[fail] 清單與 AutoDuty/Paths 不同步（--check 模式不寫檔）")
        return 1

    tmp = MD5_JSON + ".tmp"
    open(tmp, "wb").write(blob)
    os.replace(tmp, MD5_JSON)
    print("已重寫 %s（%d bytes）" % (MD5_JSON, len(blob)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
