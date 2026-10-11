"""检查界面翻译：缺少英文的条目、没被用到的条目、重复的条目，以及还没包进 T() 的中文。

用法：python -I tools/check_lang.py（在仓库根目录运行），全部为 0 时退出码为 0。
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
HAN = re.compile(r"[\u4e00-\u9fff]")
STR = r'"((?:[^"\\]|\\.)*)"'
# 语言选项原名；GPT-SoVITS 预热用的合成文本；主页按内部状态值前缀判断加载圈
IGNORE = {"简体中文", "预热。", "合成", "等待", "切换"}

CS_DICT = ROOT / "ui" / "Lang.En.cs"
PY = ROOT / "gsv_server.py"


def unesc(s):
    """C# 字符串字面量 → 实际文字，和 XAML 里的原文比较。"""
    return s.replace("\\\\", "\\").replace('\\"', '"')


def strip_cs(line):
    if line.lstrip().startswith("//"):
        return ""
    return re.sub(r"(?<![:\"])//(?!/).*$", "", line)


def py_code_lines(text):
    """去掉三引号文档字符串和 # 注释后的 (行号, 原行, 代码) 列表。"""
    out, in_doc = [], False
    for i, line in enumerate(text.splitlines(), 1):
        s = line.strip()
        if s.count('"""') == 1:
            in_doc = not in_doc
            continue
        if in_doc or s.startswith('"""'):
            continue
        out.append((i, line, re.sub(r"\s#\s.*$", "", line)))
    return out


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    problems = 0

    # ---------- C# / XAML ----------
    dict_text = CS_DICT.read_text(encoding="utf-8")
    keys = [unesc(k) for k in re.findall(r'\[' + STR + r'\]\s*=', dict_text)]
    cs_dict = set(keys)
    dups = sorted({k for k in keys if keys.count(k) > 1})

    used, unwrapped = set(), []
    files = [p for p in (ROOT / "ui").rglob("*") if p.suffix in (".cs", ".xaml")
             and not {"bin", "obj"} & set(p.parts) and p != CS_DICT]
    for p in sorted(files):
        for i, line in enumerate(p.read_text(encoding="utf-8").splitlines(), 1):
            if p.suffix == ".cs":
                code = strip_cs(line)
                if "// 翻译键" in line:
                    used.update(unesc(k) for k in re.findall(STR, code) if HAN.search(k))
                    continue
                found = [unesc(k) for k in re.findall(r"\bT\(" + STR, code)]
                rest = re.sub(r"\bT\(" + STR, "", code)
            else:
                code = re.sub(r"<!--.*?-->", "", line)
                found = re.findall(r"\{l:T Text='([^']*)'\}", code)
                rest = re.sub(r"\{l:T Text='[^']*'\}", "", code)
            used.update(found)
            for lit in re.findall(STR, rest):
                if HAN.search(lit) and lit not in IGNORE:
                    unwrapped.append(f"{p.relative_to(ROOT)}:{i}: {lit}")

    # ---------- Python ----------
    py_text = PY.read_text(encoding="utf-8")
    m = re.search(r"_EN = \{(.*?)\n\}", py_text, re.S)
    py_keys = re.findall(STR + r"\s*:", m.group(1)) if m else []
    py_dict = set(py_keys)
    dups += sorted({k for k in py_keys if py_keys.count(k) > 1})
    py_used, py_unwrapped = set(), []
    dict_span = (py_text[:m.start()].count("\n") + 1, py_text[:m.end()].count("\n") + 1) if m else (0, 0)
    for i, line, code in py_code_lines(py_text):
        if dict_span[0] <= i <= dict_span[1]:
            continue
        if "# 翻译键" in line:
            py_used.update(k for k in re.findall(STR, line) if HAN.search(k))
            continue
        found = re.findall(r"_t\(" + STR, code)
        py_used.update(found)
        rest = re.sub(r"_t\(" + STR, "", code)
        for lit in re.findall(STR, rest):
            if HAN.search(lit) and lit not in IGNORE:
                py_unwrapped.append(f"gsv_server.py:{i}: {lit}")

    report = [
        ("缺少英文（C#/XAML）", sorted(used - cs_dict)),
        ("没被用到（Lang.En.cs）", sorted(cs_dict - used)),
        ("缺少英文（gsv_server.py）", sorted(py_used - py_dict)),
        ("没被用到（gsv_server.py _EN）", sorted(py_dict - py_used)),
        ("重复的条目", dups),
        ("没包进 T() 的中文", unwrapped + py_unwrapped),
    ]
    for title, items in report:
        print(f"{title}：{len(items)}")
        for it in items:
            print(f"  {it}")
        problems += len(items)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
