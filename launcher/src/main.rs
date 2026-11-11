//! VoiceMikon 启动壳 —— 一个「真的应用」外壳。
//!
//! # 为什么要有这个壳
//!
//! 第三方剪贴板管理器(Ditto 一类)给剪贴板内容标注来源时,走的是:
//! ```text
//! GetClipboardOwner() -> HWND -> GetWindowThreadProcessId() -> PID
//!   -> QueryFullProcessImageNameW() -> 完整路径 -> basename
//! ```
//! 它取的是**真正在跑 Python 的那个进程的映像文件名**。原来用
//! `pythonw.exe + scripts\launch.pyw`,标注出来就是 "Pythonw.exe"。
//!
//! 所以壳必须**自己就是跑 Python 的那个进程**(把 CPython 装进本进程),
//! 而不是 `CreateProcess` 去拉起 pythonw —— 那样子进程还是 pythonw.exe,
//! 标注不会变。
//!
//! # 运行时数据从哪来
//!
//! - `runtime\pyvenv.cfg`(exe 的**父目录**,CPython 只在 exe 目录和父目录找它,
//!   父目录优先)→ 决定 `sys.prefix` = runtime\,`sys.base_prefix` = base 解释器目录
//! - `runtime\launcher.toml` → 真实 venv 的 site-packages,用 `site.addsitedir` 挂载
//!   (`.pth` 只在 site 初始化时被处理 —— 目录塞进 PYTHONPATH 进得了
//!   `sys.path`,但里面的 `.pth` 不会被执行,挂不得)
//!
//! # 失败兜底
//!
//! 任何一步失败都:写 `data\launcher.log` + 弹 MessageBox + 非 0 退出。
//! **绝不静默退出** —— 本程序是 GUI 子系统,没有控制台,静默 = 用户以为双击了没反应。

#![windows_subsystem = "windows"]

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};

// ---------------------------------------------------------------- Win32 FFI
// 运行期零 crate 依赖:只用 kernel32 / user32 的几个函数,手写 extern "system"。

type Hmodule = isize;
type Hwnd = isize;
type Lpcwstr = *const u16;
type Farproc = isize;

const MB_OK: u32 = 0x0000_0000;
const MB_ICONERROR: u32 = 0x0000_0010;

#[link(name = "kernel32")]
extern "system" {
    fn LoadLibraryW(lp_lib_file_name: Lpcwstr) -> Hmodule;
    fn GetProcAddress(h_module: Hmodule, lp_proc_name: *const u8) -> Farproc;
    fn SetDllDirectoryW(lp_path_name: Lpcwstr) -> i32;
    fn GetLastError() -> u32;
    fn GetLocalTime(lp_system_time: *mut SystemTime);
}

/// Win32 SYSTEMTIME(windows::Foundation::SYSTEMTIME 的同构体)。
#[repr(C)]
struct SystemTime {
    year: u16,
    month: u16,
    day_of_week: u16,
    day: u16,
    hour: u16,
    minute: u16,
    second: u16,
    milliseconds: u16,
}

#[link(name = "user32")]
extern "system" {
    fn MessageBoxW(
        h_wnd: Hwnd,
        lp_text: Lpcwstr,
        lp_caption: Lpcwstr,
        u_type: u32,
    ) -> i32;
}

// ------------------------------------------------------------ CPython 的类型
// 不链接 python312.lib(uv 发行版没带 .lib/头文件),全部运行时 GetProcAddress 解析。

type FnInitialize = unsafe extern "system" fn();
type FnFinalize = unsafe extern "system" fn();
type FnSetArgvEx = unsafe extern "system" fn(i32, *const *mut u16) -> i32;
type FnRunSimpleString = unsafe extern "system" fn(*const u8) -> i32;

// ------------------------------------------------------------------ 小工具

/// 宽字符串(UTF-16,带结尾 NUL)。
fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

/// C 字符串(UTF-8 字节,带结尾 NUL)。
fn cstr(s: &str) -> Vec<u8> {
    let mut v = s.as_bytes().to_vec();
    v.push(0);
    v
}

/// 把字符串变成 Python 源码里的字符串字面量(转义反斜杠 / 引号 / 控制字符)。
fn py_quote(s: &str) -> String {
    let mut out = String::from("\"");
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\x{:02x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

/// 当前时间的本地可读戳。
///
/// 用本地时间而不是 UTC —— 启动器的日志要和 data\voice_input.log(Python 的
/// logging,本地时间)对照着看,差 8 小时会让人以为出了时区问题。
fn now_iso() -> String {
    let mut st = SystemTime {
        year: 0,
        month: 0,
        day_of_week: 0,
        day: 0,
        hour: 0,
        minute: 0,
        second: 0,
        milliseconds: 0,
    };
    unsafe {
        GetLocalTime(&mut st);
    }
    format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}",
        st.year, st.month, st.day, st.hour, st.minute, st.second
    )
}

/// 追加一行日志。日志路径在项目内的 data\ —— portable 约定,见仓库根 AGENTS.md。
fn log_append(path: &Path, msg: &str) {
    use std::io::Write;
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)
    {
        let _ = writeln!(f, "[{}] {}", now_iso(), msg);
        let _ = f.flush();
    }
}

/// 取日志末尾 n 行(给 MessageBox 用)。
fn log_tail(path: &Path, n: usize) -> String {
    let Ok(text) = std::fs::read_to_string(path) else {
        return String::from("(日志文件读不到)");
    };
    let lines: Vec<&str> = text.lines().collect();
    let start = lines.len().saturating_sub(n);
    lines[start..].join("\n")
}

/// 弹错误框。**启动失败必须让人看见** —— 没有控制台,静默 = 神秘闪退。
fn message_box(text: &str) {
    let t = wide(text);
    let c = wide("Voice Mikon - 启动失败");
    unsafe {
        MessageBoxW(0, t.as_ptr(), c.as_ptr(), MB_OK | MB_ICONERROR);
    }
}

/// 启动失败:记日志 + 弹框 + 非 0 退出。
fn fail(log: &Path, reason: &str) -> ! {
    log_append(log, &format!("启动失败: {reason}"));
    let detail = log_tail(log, 20);
    message_box(&format!(
        "{reason}\n\n--- 最近日志(data\\launcher.log) ---\n{detail}"
    ));
    std::process::exit(1);
}

/// 取导出函数地址。FARPROC 和具体函数指针同宽,直接转。
unsafe fn get_proc<T: Copy>(h: Hmodule, name: &[u8]) -> Option<T> {
    let p = GetProcAddress(h, name.as_ptr());
    if p == 0 {
        None
    } else {
        Some(std::mem::transmute_copy(&p))
    }
}

// ------------------------------------------------------------ 配置解析

/// 读 `pyvenv.cfg` 的 home 行。返回 base 解释器目录。
fn parse_pyvenv_home(cfg: &Path) -> Option<String> {
    let text = std::fs::read_to_string(cfg).ok()?;
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        let Some((k, v)) = line.split_once('=') else {
            continue;
        };
        if k.trim().eq_ignore_ascii_case("home") {
            let v = v.trim();
            if v.is_empty() {
                return None;
            }
            return Some(v.to_string());
        }
    }
    None
}

/// 从 TOML 里取一个字符串键的值(只支持双引号字符串 + 基本转义)。
fn parse_toml_string(text: &str, key: &str) -> Option<String> {
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        let Some((k, v)) = line.split_once('=') else {
            continue;
        };
        if k.trim() != key {
            continue;
        }
        let v = v.trim();
        if v.len() >= 2 && v.starts_with('"') && v.ends_with('"') {
            let inner = &v[1..v.len() - 1];
            let mut out = String::new();
            let mut it = inner.chars();
            while let Some(c) = it.next() {
                if c == '\\' {
                    match it.next() {
                        Some('\\') => out.push('\\'),
                        Some('n') => out.push('\n'),
                        Some('t') => out.push('\t'),
                        Some('r') => out.push('\r'),
                        Some('"') => out.push('"'),
                        Some(other) => {
                            out.push('\\');
                            out.push(other);
                        }
                        None => out.push('\\'),
                    }
                } else {
                    out.push(c);
                }
            }
            return Some(out);
        }
    }
    None
}

// ------------------------------------------------------------------ 主流程

fn main() {
    let exe = match std::env::current_exe() {
        Ok(p) => p,
        Err(e) => {
            message_box(&format!("连自己的路径都拿不到: {e}"));
            std::process::exit(1);
        }
    };
    let exe_dir = exe.parent().map(Path::to_path_buf).unwrap_or_default();
    // runtime\bin\VoiceMikon.exe -> runtime\  -> 项目根
    let runtime_dir = exe_dir.parent().map(Path::to_path_buf).unwrap_or_default();
    let project_dir = runtime_dir.parent().map(Path::to_path_buf).unwrap_or_default();
    let log = project_dir.join("data").join("launcher.log");

    log_append(&log, &format!("启动: {}", exe.display()));

    // --- 1. base 解释器目录(pyvenv.cfg 的 home) ---
    let cfg_path = runtime_dir.join("pyvenv.cfg");
    let Some(home) = parse_pyvenv_home(&cfg_path) else {
        fail(
            &log,
            &format!(
                "读不到 pyvenv.cfg 里的 home: {}\n(它必须在 exe 的父目录,CPython 只在 exe 目录和父目录找它)",
                cfg_path.display()
            ),
        );
    };
    log_append(&log, &format!("pyvenv.cfg home = {home}"));

    // --- 2. 真实 venv 的 site-packages ---
    let toml_path = runtime_dir.join("launcher.toml");
    let site_pkgs = std::env::var("VOICE_MIKON_VENV_SITE")
        .ok()
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .or_else(|| {
            std::fs::read_to_string(&toml_path)
                .ok()
                .and_then(|t| parse_toml_string(&t, "venv_site_packages"))
        })
        .unwrap_or_else(|| {
            fail(
                &log,
                &format!(
                    "读不到 launcher.toml 里的 venv_site_packages: {}",
                    toml_path.display()
                ),
            )
        });
    let site_path = PathBuf::from(&site_pkgs);
    if !site_path.is_dir() {
        fail(
            &log,
            &format!("venv site-packages 不存在: {}", site_path.display()),
        );
    }
    log_append(&log, &format!("venv site-packages = {site_pkgs}"));

    // --- 3. 装载 base 的 python312.dll ---
    let dll_path = PathBuf::from(&home).join("python312.dll");
    if !dll_path.is_file() {
        fail(
            &log,
            &format!("base 解释器里没有 python312.dll: {}", dll_path.display()),
        );
    }
    // 让 python312.dll 的依赖(比如 vcruntime140.dll)也能从 base 目录找到。
    let home_w = wide(&home);
    unsafe {
        SetDllDirectoryW(home_w.as_ptr());
    }
    let dll_w = wide(&dll_path.to_string_lossy());
    let dll = unsafe { LoadLibraryW(dll_w.as_ptr()) };
    if dll == 0 {
        let err = unsafe { GetLastError() };
        fail(
            &log,
            &format!("LoadLibraryW 失败(错误码 {err}): {}", dll_path.display()),
        );
    }
    log_append(&log, &format!("已加载 {}", dll_path.display()));

    // --- 4. 解析 CPython 的 4 个 API ---
    let p_initialize = unsafe { get_proc::<FnInitialize>(dll, &cstr("Py_Initialize")) };
    let p_finalize = unsafe { get_proc::<FnFinalize>(dll, &cstr("Py_Finalize")) };
    let p_setargv = unsafe { get_proc::<FnSetArgvEx>(dll, &cstr("PySys_SetArgvEx")) };
    let p_run = unsafe { get_proc::<FnRunSimpleString>(dll, &cstr("PyRun_SimpleString")) };
    if p_initialize.is_none()
        || p_finalize.is_none()
        || p_setargv.is_none()
        || p_run.is_none()
    {
        fail(&log, "python312.dll 里缺少需要的 CPython 导出函数");
    }
    let p_initialize = p_initialize.unwrap();
    let p_finalize = p_finalize.unwrap();
    let p_setargv = p_setargv.unwrap();
    let p_run = p_run.unwrap();

    // --- 5. 初始化 CPython ---
    // pyvenv.cfg 在 exe 的父目录 -> sys.prefix = runtime\,base_prefix = home。
    unsafe {
        p_initialize();
    }

    // --- 6. sys.argv[0] = exe 完整路径 ---
    // Python 侧的「当初是怎么被拉起来的」靠它判断(见 __main__._capture_entry)。
    let mut argv0 = wide(&exe.to_string_lossy());
    let argv: [*mut u16; 2] = [argv0.as_mut_ptr(), std::ptr::null_mut()];
    if unsafe { p_setargv(1, argv.as_ptr()) } != 0 {
        fail(&log, "PySys_SetArgvEx 失败");
    }

    // --- 7. 把真实 venv 的 site-packages 挂上 ---
    // 必须显式 site.addsitedir:editable 安装靠 site-packages 里的 .pth 生效,
    // 而 .pth 只在 site 初始化时被处理 —— 单纯插 sys.path 不跑 .pth。
    let add_code = format!(
        "import site\nsite.addsitedir({})\n",
        py_quote(&site_pkgs)
    );
    let add_bytes = cstr(&add_code);
    if unsafe { p_run(add_bytes.as_ptr()) } != 0 {
        fail(&log, "site.addsitedir(venv site-packages) 失败");
    }

    // --- 8. 诊断信息写进日志(prefix / executable / path) ---
    let diag_code = format!(
        "import sys\n\
         import importlib.util\n\
         _qv_d = open({log}, 'a', encoding='utf-8')\n\
         _qv_d.write('  sys.executable  = ' + repr(sys.executable) + chr(10))\n\
         _qv_d.write('  sys.prefix     = ' + repr(sys.prefix) + chr(10))\n\
         _qv_d.write('  sys.base_prefix= ' + repr(sys.base_prefix) + chr(10))\n\
         _qv_d.write('  sys.version    = ' + repr(sys.version) + chr(10))\n\
         _qv_d.write('  sys.path       = ' + repr(sys.path) + chr(10))\n\
         _qv_spec = importlib.util.find_spec('voice_mikon')\n\
         _qv_d.write('  voice_mikon 来自 = ' + repr(_qv_spec.origin if _qv_spec else None) + chr(10))\n\
         _qv_d.close()\n",
        log = py_quote(&log.to_string_lossy())
    );
    let diag_bytes = cstr(&diag_code);
    if unsafe { p_run(diag_bytes.as_ptr()) } != 0 {
        log_append(&log, "写诊断信息失败(不影响启动)");
    }

    // --- 9. 跑主程序 ---
    // 探针模式:只初始化 Python 就退出,不碰热键/托盘/窗口。
    // 用途是「壳到底把 prefix 设成了什么」的排障 —— 不用跟正在跑的实例抢热键。
    //
    // VOICE_MIKON_LAUNCHER_PROBE=clipboard 时再多做一件事:
    // 在**本进程的主线程**里建剪贴板 owner 窗口并写一次文本。
    // 这正是真实运行时的路径(见 __main__.main 的 ensure_owner_window),
    // 于是可以拿 check_owner.py 验完整条第三方取名链路:
    //     GetClipboardOwner -> PID -> 映像名 == VoiceMikon.exe
    // 停留时长由 VOICE_MIKON_PROBE_HOLD 控制(秒,默认 30),
    // 给外部脚本留出读取的时间;退出前交还所有权(内容留住,不清空)。
    let probe = std::env::var("VOICE_MIKON_LAUNCHER_PROBE").unwrap_or_default();
    if !probe.is_empty() {
        log_append(&log, &format!("探针模式: {probe}"));
        if probe.eq_ignore_ascii_case("clipboard") {
            let hold: u64 = std::env::var("VOICE_MIKON_PROBE_HOLD")
                .ok()
                .and_then(|s| s.trim().parse().ok())
                .unwrap_or(30);
            let code = format!(
                "import time\n\
                 from voice_mikon.win32 import clipboard\n\
                 _qv_hwnd = clipboard.ensure_owner_window()\n\
                 _qv_ok = clipboard.set_text({text})\n\
                 _qv_f = open({log}, 'a', encoding='utf-8')\n\
                 _qv_f.write('  剪贴板自检: hwnd=' + str(_qv_hwnd) + ' set_text=' + str(_qv_ok) + chr(10))\n\
                 _qv_f.close()\n\
                 time.sleep({hold})\n\
                 clipboard.release_ownership()\n",
                text = py_quote("VoiceMikon.exe 剪贴板 owner 自检"),
                log = py_quote(&log.to_string_lossy()),
                hold = hold
            );
            let probe_bytes = cstr(&code);
            if unsafe { p_run(probe_bytes.as_ptr()) } != 0 {
                fail(&log, "剪贴板自检失败(见日志)");
            }
            log_append(&log, "剪贴板自检结束");
        }
        if probe.eq_ignore_ascii_case("restart") {
            // 在**真实的 exe 入口上下文**里跑一遍托盘「重启」用的那两个函数:
            //     _capture_entry() -> _restart_plan(entry)
            // argv0 就是 exe 完整路径、frozen=False,正是新分支的判定条件。
            // 设 VOICE_MIKON_PROBE_RESTART=1 时还会照 _spawn_restart 的方式
            // (DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP)真的拉起新实例。
            let spawn = if std::env::var_os("VOICE_MIKON_PROBE_RESTART").is_some() {
                "True"
            } else {
                "False"
            };
            let code = format!(
                "import os, subprocess\n\
                 from voice_mikon.__main__ import _capture_entry, _restart_plan\n\
                 _qv_entry = _capture_entry()\n\
                 _qv_cmd, _qv_cwd, _qv_note = _restart_plan(_qv_entry)\n\
                 _qv_f = open({log}, 'a', encoding='utf-8')\n\
                 _qv_f.write('  重启计划 entry: ' + repr(_qv_entry) + chr(10))\n\
                 _qv_f.write('  重启计划 cmd  : ' + repr(_qv_cmd) + chr(10))\n\
                 _qv_f.write('  重启计划 cwd  : ' + repr(_qv_cwd) + chr(10))\n\
                 _qv_f.write('  重启计划 note : ' + repr(_qv_note) + chr(10))\n\
                 _qv_f.close()\n\
                 _qv_pid = None\n\
                 if {spawn} and os.environ.get('VOICE_MIKON_PROBE_DEPTH') is None:\n\
                 \x20   _qv_env = dict(os.environ)\n\
                 \x20   for _qv_k in ('VOICE_MIKON_LAUNCHER_PROBE', 'VOICE_MIKON_PROBE_RESTART', 'VOICE_MIKON_PROBE_HOLD'):\n\
                 \x20       _qv_env.pop(_qv_k, None)\n\
                 \x20   _qv_env['VOICE_MIKON_PROBE_DEPTH'] = '1'\n\
                 \x20   _qv_flags = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP\n\
                 \x20   _qv_pid = subprocess.Popen(_qv_cmd, cwd=_qv_cwd, env=_qv_env, creationflags=_qv_flags, close_fds=True).pid\n\
                 _qv_f = open({log}, 'a', encoding='utf-8')\n\
                 _qv_f.write('  重启计划 新实例 PID: ' + repr(_qv_pid) + chr(10))\n\
                 _qv_f.close()\n",
                log = py_quote(&log.to_string_lossy()),
                spawn = spawn
            );
            let probe_bytes = cstr(&code);
            if unsafe { p_run(probe_bytes.as_ptr()) } != 0 {
                fail(&log, "重启计划自检失败(见日志)");
            }
            log_append(&log, "重启计划自检结束");
        }
        unsafe {
            p_finalize();
        }
        std::process::exit(0);
    }

    // 异常在这里被 Python 侧接住并写进日志,壳再把日志尾巴弹出来 ——
    // GUI 子系统没有 stderr,Python 的 traceback 没人看得见。
    let main_code = format!(
        "import sys, traceback\n\
         _qv_log = {log}\n\
         try:\n\
         \x20   from voice_mikon.__main__ import main\n\
         \x20   _qv_rc = main()\n\
         except SystemExit as _qv_e:\n\
         \x20   _qv_rc = _qv_e.code if isinstance(_qv_e.code, int) else 0\n\
         except BaseException:\n\
         \x20   try:\n\
         \x20       with open(_qv_log, 'a', encoding='utf-8') as _qv_f:\n\
         \x20           _qv_f.write(traceback.format_exc())\n\
         \x20   except Exception:\n\
         \x20       pass\n\
         \x20   raise\n\
         sys.exit(_qv_rc)\n",
        log = py_quote(&log.to_string_lossy())
    );
    let main_bytes = cstr(&main_code);
    log_append(&log, "开始跑 voice_mikon.__main__.main()");
    let rc = unsafe { p_run(main_bytes.as_ptr()) };
    if rc != 0 {
        log_append(&log, "PyRun_SimpleString 返回负数 —— Python 侧抛异常了");
        let detail = log_tail(&log, 30);
        message_box(&format!(
            "Python 侧启动失败。\n\n--- 最近日志(data\\launcher.log) ---\n{detail}"
        ));
        unsafe {
            p_finalize();
        }
        std::process::exit(1);
    }

    // 正常路径下 sys.exit() 已经把进程带走了(走 CPython 的 Py_Exit -> exit())。
    // 能走到这里说明 main() 自己退出了而没有走 sys.exit —— 兜底收尾。
    log_append(&log, "主程序已退出");
    unsafe {
        p_finalize();
    }
    std::process::exit(0);
}
