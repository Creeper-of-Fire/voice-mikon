"""二维码/条形码解码:pyzbar(zbar) 的薄包装。

截图场景的码一般清晰、正对,zbar 一次就中。识别不到时按
「原图 → 反色 → 放大一倍」的顺序各试一遍(反色救"黑底白码",
放大救轻度模糊),还不行就如实返回空 —— 不做更重的预处理
(opencv 之类),那是另一个量级的依赖。

条形码也顺手认(zbar 本来就都认),结果窗如实标注符号类型。
"""

from __future__ import annotations

from dataclasses import dataclass

from PIL import Image, ImageOps


@dataclass(frozen=True)
class QrHit:
    """一次解码命中。"""

    text: str  # 解出的文本;非 utf-8 字节用替换字符保底,绝不抛异常
    kind: str  # 符号类型,如 QRCODE / CODE128


def decode_image(image: Image.Image) -> list[QrHit]:
    """在图里找码,返回全部命中(通常 0 或 1 个)。

    候选版本逐个试,某一遍有命中就停 —— 避免重复解码同一个码。
    """
    seen: set[str] = set()
    hits: list[QrHit] = []
    for candidate in _candidate_versions(image):
        found = _zbar_decode(candidate)
        for item in found:
            # zbar 给的是 bytes;二维码内容按约定是 utf-8,
            # 真遇上别的编码也先展示出来(替换坏字节)再说
            text = item.data.decode("utf-8", errors="replace")
            if text not in seen:
                seen.add(text)
                hits.append(QrHit(text=text, kind=str(item.type)))
        if hits:
            break
    return hits


def _candidate_versions(image: Image.Image) -> list[Image.Image]:
    """逐级补救的候选图:原图 → 反色 → 两倍放大。"""
    versions = [image]
    try:
        versions.append(ImageOps.invert(image.copy()))
    except OSError:
        # invert 只吃常规 mode;真遇上了就跳过这一档
        pass
    versions.append(
        image.resize((image.width * 2, image.height * 2), Image.LANCZOS)
    )
    return versions


def _zbar_decode(image: Image.Image):
    """pyzbar 延迟导入:把「依赖没装」的报错留到真正解码时。"""
    from pyzbar.pyzbar import decode

    return decode(image)
