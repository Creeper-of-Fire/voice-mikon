"""术语表(热词)加载测试。

热词是**用户数据** —— 加载失败不能拖垮整个应用,
文件不存在是正常情况(没有术语而已)。
"""

from pathlib import Path

from qwen3_voice.config import Config, load_hotwords_file


def test_missing_file_is_not_an_error(tmp_path: Path):
    """文件不存在 -> 空列表,不抛异常。"""
    assert load_hotwords_file(tmp_path / "nope.txt") == []


def test_reads_one_word_per_line(tmp_path: Path):
    f = tmp_path / "hotwords.txt"
    f.write_text("Qwen3-ASR\nPlasmaService\nFokkerPlanckBBN\n", encoding="utf-8")
    assert load_hotwords_file(f) == ["Qwen3-ASR", "PlasmaService", "FokkerPlanckBBN"]


def test_skips_comments_and_blank_lines(tmp_path: Path):
    f = tmp_path / "hotwords.txt"
    f.write_text("# 这是注释\n\nQwen3-ASR\n\n# 又一条注释\nPlasmaService\n",
                 encoding="utf-8")
    assert load_hotwords_file(f) == ["Qwen3-ASR", "PlasmaService"]


def test_strips_whitespace(tmp_path: Path):
    f = tmp_path / "hotwords.txt"
    f.write_text("  Qwen3-ASR  \n\tPlasmaService\t\n", encoding="utf-8")
    assert load_hotwords_file(f) == ["Qwen3-ASR", "PlasmaService"]


def test_deduplicates(tmp_path: Path):
    f = tmp_path / "hotwords.txt"
    f.write_text("Qwen3-ASR\nQwen3-ASR\nPlasmaService\n", encoding="utf-8")
    assert load_hotwords_file(f) == ["Qwen3-ASR", "PlasmaService"]


def test_keeps_order(tmp_path: Path):
    f = tmp_path / "hotwords.txt"
    f.write_text("zeta\nalpha\nmid\n", encoding="utf-8")
    assert load_hotwords_file(f) == ["zeta", "alpha", "mid"]


def test_empty_file(tmp_path: Path):
    f = tmp_path / "hotwords.txt"
    f.write_text("", encoding="utf-8")
    assert load_hotwords_file(f) == []


def test_handles_utf8_chinese_terms(tmp_path: Path):
    """中文术语也要能读(用户可能直接写中文词)。"""
    f = tmp_path / "hotwords.txt"
    f.write_text("氟聚醚\n聚四氟乙烯\n", encoding="utf-8")
    assert load_hotwords_file(f) == ["氟聚醚", "聚四氟乙烯"]
