// 构建期把图标 + 版本资源嵌进 exe。
//
// 图标是项目里现成的 7 尺寸多分辨率 ico(16/24/32/48/64/128/256),
// 路径相对于本文件(CARGO_MANIFEST_DIR),所以写 "../src/..."。
//
// 注意:剪贴板管理器显示的是**映像文件名**(VoiceMikon.exe),不是版本资源里的
// FileDescription —— 版本资源只影响任务管理器「属性」页和资源管理器图标。

fn main() {
    let icon = "../src/voice_mikon/host/static/app.ico";
    println!("cargo:rerun-if-changed={icon}");

    winresource::WindowsResource::new()
        .set_icon(icon)
        .set("FileDescription", "Voice Mikon")
        .set("ProductName", "VoiceMikon")
        .set("CompanyName", "VoiceMikon")
        .set("FileVersion", "0.1.0")
        .set("ProductVersion", "0.1.0")
        .set("LegalCopyright", "AGPL-3.0-or-later")
        .compile()
        .unwrap();
}
