#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-"$ROOT_DIR/dist/skills"}"
# 只读 VERSION 第一行作为版本号，第二行是日期
VERSION="$(head -n 1 "$ROOT_DIR/VERSION" | tr -d '[:space:]')"

if ! command -v python3 >/dev/null 2>&1; then
  echo "error: python3 command is required" >&2
  exit 1
fi

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

INNER_DIR="$(mktemp -d)"
trap 'rm -rf "$INNER_DIR"' EXIT

# skill 名 → 分组目录
group_for() {
  case "$1" in
    tdskill)
      echo "必装入口" ;;
    tds-attraction|tds-display)
      echo "认知与展示" ;;
    tds-approach|tds-chat|tds-date)
      echo "互动与连接" ;;
    tds-relationship)
      echo "关系管理" ;;
    tds-learn)
      echo "学习方法" ;;
    *)
      echo "未分组" ;;
  esac
}

build_one() {
  local skill_dir="$1"
  local name
  local group
  local stage_dir
  local target_dir

  name="$(basename "$skill_dir")"
  group="$(group_for "$name")"
  target_dir="$INNER_DIR/$group"
  mkdir -p "$target_dir"

  stage_dir="$(mktemp -d)"

  # 复制 SKILL.md
  cp "$skill_dir/SKILL.md" "$stage_dir/SKILL.md"

  # 复制同目录下的方法论文档（xxx_方法论.md）
  for md in "$skill_dir"/*_*.md; do
    [[ -f "$md" ]] || continue
    cp "$md" "$stage_dir/$(basename "$md")"
  done

  # 复制可选子目录
  for subdir in templates docs tools scripts; do
    if [ -d "$skill_dir/$subdir" ]; then
      mkdir -p "$stage_dir/$subdir"
      cp -R "$skill_dir/$subdir/." "$stage_dir/$subdir/"
    fi
  done

  # 打包
  python3 - "$stage_dir" "$target_dir/${name}.zip" <<'PY'
import os
import sys
import zipfile

source_dir, archive_path = sys.argv[1], sys.argv[2]

with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
    for root, _, files in os.walk(source_dir):
        for filename in files:
            path = os.path.join(root, filename)
            archive.write(path, os.path.relpath(path, source_dir))
PY

  rm -rf "$stage_dir"
  echo "built $group/${name}.zip"
}

# 1. 打包主入口（根目录 SKILL.md）
build_one "$ROOT_DIR"

# 2. 打包子 skill（references/ 目录下）
for skill_md in "$ROOT_DIR"/references/*/SKILL.md; do
  skill_dir="$(dirname "$skill_md")"
  skill_name="$(basename "$skill_dir")"

  if [[ "$skill_name" == *beta* ]]; then
    echo "skipped local-only beta skill: $skill_name"
    continue
  fi

  build_one "$skill_dir"
done

# 生成分组说明
cat > "$INNER_DIR/README.md" <<EOF
# tdskill ${VERSION}

一个 zip 装一个 skill。本压缩包按使用场景分了几个文件夹，按需把里面的 zip 逐个拖进 Agent 的「上传技能」窗口即可。

## 必装入口

- **tdskill** — 主入口，根据你的问题自动路由到合适的诊断 skill。其他 skill 都依赖它，先装这个。

## 认知与展示

- **tds-attraction** — 认知提升诊断（找到吸引力层面的认知卡点）
- **tds-display** — 展示面与人设打造诊断（检查信号传递是否正确）

## 互动与连接

- **tds-approach** — 搭讪诊断（自然地开始一次交流）
- **tds-chat** — 聊天诊断（建立真正有连接的对话）
- **tds-date** — 约会诊断（把约会变成有效的情感传递）

## 关系管理

- **tds-relationship** — 关系管理诊断（建立可持续发展的长期关系）

## 学习方法

- **tds-learn** — 学习方法诊断（把知识转化为行为改变）

---

每个 zip 解压后根级是 SKILL.md（带 YAML frontmatter，含 name + description），格式遵循 Anthropic Skills 规范。
EOF

# 打包总 zip
python3 - "$INNER_DIR" "$OUT_DIR/tdskill-${VERSION}.zip" <<'PY'
import os
import sys
import zipfile

inner_dir, archive_path = sys.argv[1], sys.argv[2]

with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
    for root, _, files in os.walk(inner_dir):
        for filename in sorted(files):
            path = os.path.join(root, filename)
            archive.write(path, os.path.relpath(path, inner_dir))
PY

echo
echo "done: $OUT_DIR/tdskill-${VERSION}.zip"
