"""pytest 的全局配置(目前只有标记注册)。

为什么要有这个文件:仓库里没有 CI,测试全靠本地跑,
所以**约定必须写在这里**,不然过几个月谁也不记得:

* ``focus`` —— 会抢用户前台焦点的用例。
  这类用例默认**不跑**:抢了焦点,用户正在打的字就没了。
  要跑就显式设环境变量 ``QWEN3_VOICE_TEST_FOCUS=1``。
  标记本身只是给人看的(``pytest --markers`` 查得到),
  真正的开关是那个环境变量。

新增这类标记记得在这里登记,否则 pytest 会报
"PytestUnknownMarkWarning"。
"""


def pytest_configure(config):
    config.addinivalue_line(
        "markers",
        "focus: 会抢用户前台焦点的用例,默认不跑;"
        "设 QWEN3_VOICE_TEST_FOCUS=1 才跑",
    )
