"""验证 check-structure.py 的 [5] 编码约定检查确实会报错。

做法：在临时目录里造一个最小仓库，逐个注入违规，确认检查能抓到。
这样检查本身也有测试——否则可能加了一条永远不会失败的规则。
"""
import io
import os
import re
import shutil
import subprocess
import sys
import tempfile

# Windows 控制台默认 GBK，会把中文输出写成乱码甚至抛错
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# 从脚本位置推导，不写死本机路径——否则别人克隆下来直接跑不起来
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PY = sys.executable

# 每个用例：(名称, 文件名, 文件内容, 期望命中的关键字或 None=应放行)
# 关键字取英文片段：中文在 GBK 控制台下不可靠，且改文案时不必同步改测试
CASES = [
    ('dash 分割线', 'A.cs',
     'namespace T;\n\npublic sealed class A\n{\n    // ---- 分组\n    public int X;\n}\n',
     '#region'),

    ('Service 泛化后缀', 'B.cs',
     'namespace T;\n\ninternal sealed class ThingService\n{\n}\n',
     'Service'),

    ('HostedService 应放行', 'C.cs',
     'namespace T;\n\npublic sealed class MyHostedService\n{\n}\n',
     None),

    ('嵌套日志类', 'D.cs',
     'namespace T;\n\ninternal static partial class D\n{\n'
     '    private static partial class Log\n    {\n    }\n}\n',
     'Log'),

    ('region 缩进一致应放行', 'E.cs',
     'namespace T;\n\npublic sealed class E\n{\n'
     '    #region 一组\n    public int X;\n    #endregion\n}\n',
     None),

    ('region 缩进不一致', 'E2.cs',
     'namespace T;\n\npublic sealed class E2\n{\n'
     '    #region 一组\n    public int X;\n#endregion\n}\n',
     '#endregion'),

    ('region 未闭合', 'F.cs',
     'namespace T;\n\npublic sealed class F\n{\n    #region 一组\n    public int X;\n}\n',
     '#endregion'),

    ('TODO 标记', 'G.cs',
     'namespace T;\n\npublic sealed class G\n{\n    // TODO 以后再说\n    public int X;\n}\n',
     'TODO'),

    ('csproj SDK 错误', 'H.csproj',
     '<Project Sdk="Microsoft.NET.Sdk.Web">\n</Project>\n',
     'SDK'),

    ('接口成员缺 XML 文档', 'I.cs',
     'namespace T;\n\npublic interface IThing\n{\n    void DoWork();\n}\n',
     'XML'),

    ('接口成员有 XML 文档应放行', 'J.cs',
     'namespace T;\n\npublic interface IThing2\n{\n    /// <summary>做事。</summary>\n'
     '    void DoWork();\n}\n',
     None),

    ('csproj 含注释', 'K.csproj',
     '<Project Sdk="Microsoft.NET.Sdk">\n'
     '  <!-- 这里不该有注释 -->\n'
     '  <PropertyGroup>\n    <AssemblyName>K</AssemblyName>\n  </PropertyGroup>\n</Project>\n',
     'csproj'),

    ('csproj 无注释应放行', 'K2.csproj',
     '<Project Sdk="Microsoft.NET.Sdk">\n'
     '  <PropertyGroup>\n    <AssemblyName>K2</AssemblyName>\n  </PropertyGroup>\n</Project>\n',
     None),

    ('行内注释块超长', 'L.cs',
     'namespace T;\n\npublic sealed class L\n{\n'
     '    // one\n    // two\n    // three\n    // four\n    // five\n'
     '    public int X;\n}\n',
     'README/AGENTS/DEVELOPMENT'),

    ('行内注释 4 行应放行', 'L2.cs',
     'namespace T;\n\npublic sealed class L2\n{\n'
     '    // 一行\n    // 二行\n    // 三行\n    // 四行\n'
     '    public int X;\n}\n',
     None),

    ('连续 XML 文档注释不受行数限制', 'L3.cs',
     'namespace T;\n\npublic sealed class L3\n{\n'
     '    /// <summary>\n    /// 一\n    /// 二\n    /// 三\n    /// 四\n    /// 五\n    /// 六\n    /// </summary>\n'
     '    public int X;\n}\n',
     None),
]


def run_case(name, filename, content, expect):
    tmp = tempfile.mkdtemp(prefix='ncchk-')
    try:
        proj = os.path.join(tmp, 'NcLink')
        os.makedirs(proj)
        io.open(os.path.join(proj, filename), 'w', encoding='utf-8').write(content)

        # 复制检查脚本，并把 REPO_ROOT 指到临时目录
        src = io.open(os.path.join(REPO, 'tools', 'check-structure.py'), encoding='utf-8').read()
        src = src.replace(
            "REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))",
            f"REPO_ROOT = {tmp!r}")
        # PROJECTS 是多行声明，必须整体替换——只匹配首行会留下续行变成语法错误
        src, n = re.subn(r"PROJECTS = \[[^\]]*\]", "PROJECTS = ['NcLink']", src)
        assert n == 1, f'PROJECTS 替换次数异常: {n}'
        tool = os.path.join(tmp, 'check.py')
        io.open(tool, 'w', encoding='utf-8').write(src)

        r = subprocess.run([PY, tool], capture_output=True, text=True,
                           encoding='utf-8', errors='replace', cwd=tmp)
        out = (r.stdout or '') + (r.stderr or '')
        # [5] 段落里列出违规明细；用它判断是否命中
        style_section = out.split('[5]')[-1] if '[5]' in out else ''

        if expect is None:
            ok = '[5]' in out and '0' in style_section.split('\n')[0]
            detail = '应放行 -> 通过' if ok else f'被误报: {style_section.strip()[:70]}'
        else:
            ok = expect in style_section
            detail = '已抓到' if ok else f'未抓到（期望含 {expect}）: {style_section.strip()[:70]}'
        print(f'  [{"OK  " if ok else "FAIL"}] {name}: {detail}')
        return ok
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


print(f'=== check-structure.py [5] 编码约定检查自测（{len(CASES)} 个用例）===\n')
results = [run_case(*c) for c in CASES]
good = sum(results)
print(f'\n通过 {good}/{len(results)}')
sys.exit(0 if good == len(results) else 1)

