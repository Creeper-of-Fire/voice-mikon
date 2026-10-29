"""配置模块测试。"""

import json
from pathlib import Path

from qwen3_voice.config import Config, load_config


def test_defaults_are_valid():
    cfg = Config()
    assert cfg.validate() == []
    assert cfg.model == "0.6B"
    assert cfg.hotkey == "ctrl+alt+space"


def test_unknown_keys_are_ignored():
    """旧配置文件里有多余字段时不应崩溃。"""
    cfg = Config.from_dict({"model": "1.7B", "不存在的键": 123})
    assert cfg.model == "1.7B"
    assert not hasattr(cfg, "不存在的键")


def test_validate_catches_bad_model():
    cfg = Config(model="3.0B")
    assert any("model" in issue for issue in cfg.validate())


def test_validate_catches_bad_duration():
    cfg = Config(min_duration=10, max_duration=5)
    assert any("max_duration" in issue for issue in cfg.validate())


def test_roundtrip(tmp_path: Path):
    cfg = Config(model="1.7B", hotkey="ctrl+shift+k", panel_geometry="10,20,300,400")
    target = tmp_path / "config.json"
    cfg.save(target)

    assert target.is_file()
    loaded = Config.from_dict(json.loads(target.read_text(encoding="utf-8")))
    assert loaded.model == "1.7B"
    assert loaded.hotkey == "ctrl+shift+k"
    assert loaded.panel_geometry == "10,20,300,400"


def test_load_config_falls_back_on_broken_file(tmp_path: Path):
    broken = tmp_path / "broken.json"
    broken.write_text("{ 这不是合法 json", encoding="utf-8")
    cfg = load_config(broken)
    # 坏文件应被跳过,拿到默认值而不是抛异常
    assert isinstance(cfg, Config)
    assert cfg.validate() == []


def test_update_ignores_unknown():
    cfg = Config()
    cfg.update(model="1.7B", 不存在=1)
    assert cfg.model == "1.7B"


def test_device_name_hints_not_shared_between_instances():
    """可变默认值不能共享 —— 否则改一个会污染另一个。"""
    a = Config()
    b = Config()
    a.device_name_hints.append("X")
    assert "X" not in b.device_name_hints


# ---------------- 主题档位 ----------------

def test_theme_defaults_to_auto():
    """默认"跟随系统" —— 新装的用户不该被钉死在某一套配色上。"""
    assert Config().theme == "auto"


def test_theme_roundtrip(tmp_path: Path):
    """主题要能存进 config.json 再读回来。"""
    cfg = Config(theme="light")
    target = tmp_path / "config.json"
    cfg.save(target)

    loaded = Config.from_dict(json.loads(target.read_text(encoding="utf-8")))
    assert loaded.theme == "light"


def test_validate_catches_bad_theme():
    """写坏了的主题档位要能被发现(不然界面会跟着瞎切)。"""
    cfg = Config(theme="solarized")
    assert any("theme" in issue for issue in cfg.validate())


def test_valid_themes_pass_validation():
    for mode in ("auto", "light", "dark"):
        assert Config(theme=mode).validate() == []
