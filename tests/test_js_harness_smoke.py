"""前端静态资产的冒烟测试。

两个 harness(``capsule_js_harness.js`` / ``panel_js_harness.js``)跑的是
**真的**前端 JS —— 它们用 node 造一个最小 DOM,把 ``ui/static/*.js``
加载进去跑。

问题在于:harness 本身**不被 pytest 收集**(pytest 默认只收 ``test_*.py``),
只被 ``tests/test_ui_windows.py`` 里的两处调用点执行。那两处调用点要是
被改名、删掉、或者路径写错,harness 就静默退化成死代码 —— pytest 一声不吭,
前端再坏也没人知道。

所以这里补一个**直接跑 harness** 的冒烟测试:
返回码必须是 0,stdout 必须是能解析的 JSON,而且关键字段要在。
harness 自己语法错、路径写错、前端 JS 改了签名,都会在这里报出来。

顺带做一条 CSS 的静态检查(见下面那条用例)。
"""

import json
import re
import shutil
import subprocess
from pathlib import Path

import pytest

_TESTS_DIR = Path(__file__).resolve().parent
_STATIC_DIR = _TESTS_DIR.parent / "src" / "qwen3_voice" / "ui" / "static"

# 两个 harness 的"期望输出形状" —— 只验关键字段在,不去猜具体值
# (具体行为由 test_ui_windows.py 里的用例管,这里只管"跑得起来、说得清话")
HARNESSES = [
    ("capsule_js_harness.js", ["calls", "html", "body", "pill", "wave"]),
    ("panel_js_harness.js", ["calls", "html", "active"]),
]


def _node_or_skip() -> str:
    node = shutil.which("node")
    if not node:
        pytest.skip("没装 node,跳过前端 harness 冒烟测试")
    return node


def _run_harness(node: str, harness_name: str, steps: list[dict]) -> dict:
    """跑一个 harness,返回它吐的 JSON。"""
    harness = _TESTS_DIR / harness_name
    assert harness.exists(), f"harness 不在: {harness}"
    out = subprocess.run(
        [node, str(harness), json.dumps(steps, ensure_ascii=False)],
        capture_output=True, text=True, encoding="utf-8", timeout=60)
    assert out.returncode == 0, f"{harness_name} 跑挂了:\n{out.stderr}"
    return json.loads(out.stdout)


@pytest.mark.parametrize("harness_name,expected_keys", HARNESSES)
def test_js_harness_runs_and_emits_json(harness_name, expected_keys):
    """harness 必须跑得通,并且吐出一个说得清话的 JSON。

    这条就是给"harness 变死代码"兜底的:只要 harness 还能跑,
    ``tests/test_ui_windows.py`` 里的调用点就还有效;
    harness 哪天跑不动了,这里立刻报。
    """
    node = _node_or_skip()
    data = _run_harness(node, harness_name, [])

    assert isinstance(data, dict), f"{harness_name} 的输出不是 JSON 对象"
    for key in expected_keys:
        assert key in data, f"{harness_name} 的输出少了 {key!r}: {data}"


def test_capsule_harness_exercises_the_real_frontend():
    """胶囊 harness 真的加载并驱动了 ``capsule.js``。

    光看"输出是 JSON"不够 —— 万一 harness 自己退化成打印一个空对象,
    上面那条照样绿。所以这里走一步真实交互:调前端方法,
    看它有没有真的往 Python 那边上报。
    """
    node = _node_or_skip()
    data = _run_harness(node, "capsule_js_harness.js", [
        {"op": "call", "fn": "setPreview", "args": ["你好世界"]},
        {"op": "advance", "ms": 100},
    ])

    calls = data.get("calls", [])
    assert calls, "前端一个桥接调用都没发 —— harness 没真的跑到前端"
    assert any(c["name"] == "update_regions" for c in calls), \
        f"前端没有上报窗口区域:{calls}"


def test_panel_harness_exercises_the_real_frontend():
    """面板 harness 真的加载并驱动了 ``panel.js``。"""
    node = _node_or_skip()
    data = _run_harness(node, "panel_js_harness.js", [
        {"op": "call", "fn": "setTheme", "args": ["light"]},
    ])

    assert data.get("html", {}).get("data-theme") == "light", \
        f"前端没有设 data-theme:{data}"


# ---------------- CSS 静态检查:canvas 的 flex-basis ----------------
#
# README 那条坑:<canvas> 没有 CSS 尺寸时,默认属性尺寸是 300×150。
# 在 flex 容器里 ``flex-basis: auto`` 取的就是这个属性高度 ——
# 电平条卡片会被撑到 162px,把结果卡片挤成 0 高度,文字直接看不见。
# 必须给 canvas 一个确定的 flex-basis(``flex: 1 1 44px``)。
#
# 说明:当前前端**已经没有 canvas** 了(电平条早改成 div),
# 所以这条现在等于空转 —— 它的价值在于:哪天有人再往 HTML 里塞 canvas,
# 这里会立刻把它拦下来,而不是等到布局塌了才发现。

def _canvas_tags(html: str) -> list[str]:
    return re.findall(r"<canvas\b[^>]*>", html, flags=re.IGNORECASE)


def _css_rules(css: str) -> list[tuple[str, str]]:
    """把 CSS 拆成 [(选择器, 声明块)]。"""
    rules = []
    for match in re.finditer(r"([^{}]+)\{([^{}]*)\}", css):
        rules.append((match.group(1).strip(), match.group(2)))
    return rules


def _flex_basis_is_auto(declarations: str) -> bool | None:
    """这条声明里 flex 基准是不是 auto。

    返回 True/False;没写 flex 就返回 None(不在 flex 容器里,不受这条坑影响)。
    """
    basis = re.search(r"flex-basis\s*:\s*([^;]+);", declarations)
    if basis:
        return basis.group(1).strip().lower() == "auto"
    shorthand = re.search(r"(?<!-)\bflex\s*:\s*([^;]+);", declarations)
    if shorthand:
        parts = shorthand.group(1).split()
        if len(parts) == 3:
            return parts[2].lower() == "auto"
        if len(parts) == 2:
            # flex: <grow> <basis> —— 第二段就是基准
            return parts[1].lower() == "auto"
        return False        # flex: <grow> → 基准取 0%(由内容决定,不是 auto)
    return None


def test_canvas_has_a_definite_flex_basis():
    """HTML 里的 canvas,CSS 必须给它一个**确定的** flex 基准。

    现在前端没有 canvas,所以这条平时是空转的 —— 它是给"以后又塞了
    canvas 回来"准备的护栏。
    """
    html_files = sorted(_STATIC_DIR.glob("*.html"))
    css_files = sorted(_STATIC_DIR.glob("*.css"))
    assert html_files and css_files, f"前端静态资源不在 {_STATIC_DIR}"

    rules = []
    for css_file in css_files:
        rules.extend(_css_rules(css_file.read_text(encoding="utf-8")))

    canvases = []
    for html_file in html_files:
        for tag in _canvas_tags(html_file.read_text(encoding="utf-8")):
            hints = re.findall(r'id="([^"]+)"', tag)
            hints += [f".{c}" for c in re.findall(r'class="([^"]+)"', tag).split()]
            canvases.append((html_file.name, hints))

    for html_name, hints in canvases:
        matched = [decl for selector, decl in rules
                   if any(hint in selector for hint in hints)]
        assert matched, f"{html_name} 里的 canvas 在 CSS 里没有对应规则"
        for declarations in matched:
            assert _flex_basis_is_auto(declarations) is not True, (
                f"{html_name} 里的 canvas 用了 flex-basis:auto —— "
                f"会被它自己的默认尺寸(300×150)撑大")
