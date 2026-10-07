#!/usr/bin/env python3
"""
结构审查：把「文件夹划分是否一致」变成可重复运行的检查。

对照基准是 PlcLibrary 的划分方式——每个功能域内部有一致的「解剖结构」：
    Interfaces/   契约（接口）
    Models/       数据形状（DTO、值类型）
    Engine/       内部实现
三者不混放。

检查三类问题：
  [1] 命名空间与文件夹不符
  [2] 能力文件夹里 public 契约与 internal 实现混放
  [3] 只有一个文件的文件夹（提示可考虑并入上级）

用法（从仓库根运行）：
    python tools/check-structure.py
退出码：0 = 通过；1 = 发现问题
"""
import io
import sys

# 输出统一 UTF-8：调用方（含 tools/test-check-structure.py）按 UTF-8 读取；
# Windows 控制台默认 GBK 会把中文写成乱码。
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass
import os
import re
import sys
from collections import defaultdict

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECTS = ['NcLink', 'NcLink.Tests', 'NcLink.Simulator',
            'NcLink.Conformance', 'NcLink.ApiServer']

# 无命名空间属正常：入口 Program.cs、程序集特性、global using
NO_NAMESPACE_OK = ('Program.cs', 'AssemblyInfo.cs', 'GlobalUsings.cs')

# 允许用 Microsoft.NET.Sdk.Web 的项目（进程/宿主）。
# 其余项目必须用 Microsoft.NET.Sdk——库被误写成 .Web 是真实发生过的事故。
WEB_SDK_ALLOWED = {'NcLink.ApiServer'}

# 行内注释块的行数上限。实测 238 块里 227 块(95%) <= 3 行，
# 自然断点在 4 行；超过 4 行的全部是"把文档塞进注释"。
MAX_INLINE_COMMENT_LINES = 4


def read(path):
    return io.open(path, encoding='utf-8').read()


def scan(project_root):
    rows = []
    for dirpath, _, files in os.walk(project_root):
        if os.sep + 'bin' in dirpath or os.sep + 'obj' in dirpath:
            continue
        for f in files:
            if not f.endswith('.cs'):
                continue
            text = read(os.path.join(dirpath, f))
            m = re.search(r'namespace\s+([\w.]+)\s*[;{]', text)
            visibility = re.findall(
                r'^\s*(public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*'
                r'(?:class|interface|record|enum|struct)\s+\w+', text, re.M)
            rows.append((os.path.relpath(os.path.join(dirpath, f), project_root),
                         m.group(1) if m else None, visibility))
    return rows


def check_namespace(project_name, rows):
    problems = []
    for rel, ns, _ in rows:
        if rel.endswith(NO_NAMESPACE_OK):
            continue
        folder = os.path.dirname(rel)
        expected = project_name + ('.' + folder.replace(os.sep, '.') if folder else '')
        if ns != expected:
            problems.append((rel, expected, ns or '(none)'))
    return problems


def check_purity(project_name, rows):
    """
    Engine/ 只能放实现（internal）。这条只适用于**生产项目**。

    public 契约放 Interfaces/ + Models/；Engine/ 是内部实现，
    出现 public 类型就是放错了地方。

    注意：Interfaces/ 里同时有 public 与 internal **接口**是正常的
    （对外发布的契约 public，只给本程序集用的窄接口 internal），
    这条规则只针对 Engine/。

    测试项目不适用：xUnit 只发现 public 测试类，测试代码本就是 public。
    """
    if project_name == 'NcLink.Tests':
        return []

    by_dir = defaultdict(list)
    for rel, _, visibility in rows:
        by_dir[os.path.dirname(rel)].append((os.path.basename(rel), visibility))

    problems = []
    for d, entries in sorted(by_dir.items()):
        if not d:
            continue
        if 'Engine' not in d.split(os.sep):
            continue
        public_files = [f for f, v in entries if 'public' in v]
        if not public_files:
            continue
        internal_files = [f for f, v in entries if 'internal' in v]
        problems.append((d, public_files, internal_files))
    return problems


def check_capability_purity(project_root):
    """
    能力文件夹的内容必须单一：

      Interfaces/  只放 interface
      Models/      只放数据形状（class / record / enum / struct）
      Parsers/     只放解析行为（class），不放数据形状、不放 interface

    这是最容易退化的一条：把配套的 DTO / 选项类型顺手写在接口文件里，
    文件看着"内聚"，但 Interfaces/ 就不再是"这个能力对外提供什么"的单一答案。
    """
    problems = []
    for dirpath, _, files in os.walk(project_root):
        if os.sep + 'bin' in dirpath or os.sep + 'obj' in dirpath:
            continue
        rel_dir = os.path.relpath(dirpath, project_root)
        parts = rel_dir.split(os.sep)
        if not ({'Interfaces', 'Models', 'Parsers'} & set(parts)):
            continue
        for f in files:
            if not f.endswith('.cs'):
                continue
            text = read(os.path.join(dirpath, f))
            kinds = re.findall(
                r'^\s*(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+)*'
                r'(class|interface|record|enum|struct)\s+(\w+)', text, re.M)
            names = {k: [n for kk, n in kinds if kk == k] for k in
                     ('class', 'interface', 'record', 'enum', 'struct')}
            rel = os.path.join(rel_dir, f)
            data = names['record'] + names['enum'] + names['struct']

            if 'Interfaces' in parts:
                if names['class'] + data:
                    problems.append((rel, f'Interfaces/ 里混入数据形状: '
                                          f'{", ".join(names["class"] + data)}'))
            if 'Models' in parts:
                if names['interface']:
                    problems.append((rel, f'Models/ 里混入接口: {", ".join(names["interface"])}'))
                if names['class'] and 'Parser' in f:
                    problems.append((rel, f'Models/ 里混入解析器: {", ".join(names["class"])}'))
            if 'Parsers' in parts:
                if names['interface']:
                    problems.append((rel, f'Parsers/ 里混入接口: {", ".join(names["interface"])}'))
                if data:
                    problems.append((rel, f'Parsers/ 里混入数据形状: {", ".join(data)}'))
    return problems


def check_style(project_name, project_root):
    """
    把「写进 AGENTS.md 但容易被违反」的约定变成硬检查。

    存在的理由：同一类违规连续出现过四次，都是靠人肉发现。
    写进文档对执行者不构成约束，只有让检查失败才有效。

    检查项：
      a) 禁止 `// ----` 注释分割线（分区一律用 #region）
      b) 公开接口的成员必须有 XML 文档
      c) 类型名不得用 `-Service` 泛化后缀（框架命名约定除外）
      d) 日志类必须集中在 General/Logging/，不得嵌套在组件内
      e) #region / #endregion 缩进必须一致
      f) 不留 TODO / FIXME 等技术债标记
      g) csproj 不得含注释；SDK 必须用 Microsoft.NET.Sdk（曾误写成 .Web）
    h) 行内注释块不超过 MAX_INLINE_COMMENT_LINES 行
    """
    problems = []
    is_test = project_name == 'NcLink.Tests'

    for dirpath, _, files in os.walk(project_root):
        if os.sep + 'bin' in dirpath or os.sep + 'obj' in dirpath:
            continue
        for f in sorted(files):
            if not f.endswith('.cs'):
                continue
            rel = os.path.relpath(os.path.join(dirpath, f), project_root)
            text = read(os.path.join(dirpath, f))
            lines = text.split('\n')

            # a) dash 分割线
            for i, l in enumerate(lines, 1):
                if re.match(r'^\s*//\s*-{3,}', l):
                    problems.append((rel, i, '禁止 // ---- 分割线，分区请用 #region'))

            # b) 公开接口成员必须有 XML 文档
            #    只扫接口体内部——否则文件开头的 namespace / using 行会被误判为成员
            for im in re.finditer(r'^\s*public\s+interface\s+\w+[^{]*\{', text, re.M):
                depth, cursor = 1, im.end()
                while cursor < len(text) and depth > 0:
                    if text[cursor] == '{':
                        depth += 1
                    elif text[cursor] == '}':
                        depth -= 1
                    cursor += 1
                body = text[im.end():cursor - 1]

                for offset, l in enumerate(body.split('\n')):
                    s = l.strip()
                    # 成员形状：返回类型 + 名字 + ( 或 { 或 ;  —— 必须是方法/属性
                    if not re.match(r'^(?:Task|ValueTask|void|bool|int|long|string|double|'
                                    r'IReadOnly\w*|IEnumerable\w*|event|[\w.<>?\[\],]+\??)\s+\w+\s*[({;]',
                                    s):
                        continue
                    if not s.endswith((';', '(', '{')):
                        continue
                    prev = [x.strip() for x in body.split('\n')[max(0, offset - 6):offset]]
                    if not any(x.startswith('///') for x in prev):
                        # 换算回文件行号：接口体起始位置往后数 offset 行
                        line_no = text[:im.end()].count('\n') + 1 + offset
                        problems.append((rel, line_no,
                                         f'公开接口成员缺 XML 文档: {s[:48]}'))

            # c) 泛化后缀：名字必须以 Service 结尾（TestServiceFactory 不算），
            #    测试类（XxxTests）不适用，框架命名约定（HostedService）除外
            if not is_test:
                for m in re.finditer(
                        r'^\s*(?:public|internal)\s+(?:sealed\s+|abstract\s+|partial\s+)*'
                        r'(?:class|record)\s+(\w*Service)\b', text, re.M):
                    name = m.group(1)
                    if name.endswith('HostedService'):
                        continue   # IHostedService 的命名由框架约定决定
                    ln = text[:m.start()].count('\n') + 1
                    problems.append((rel, ln,
                                     f'类型名 {name} 用了泛化后缀 Service，'
                                     f'请改成描述"它是什么"的名词'))

            # d) 日志类必须集中在 General/Logging/
            in_logging_dir = f'{os.sep}General{os.sep}Logging{os.sep}' in os.path.join(dirpath, f) + os.sep
            if not in_logging_dir:
                for m in re.finditer(r'^\s*(?:private|internal|public)\s+static\s+partial\s+class\s+Log\b',
                                     text, re.M):
                    ln = text[:m.start()].count('\n') + 1
                    problems.append((rel, ln,
                                     '嵌套日志类 Log 必须移到 General/Logging/ 下独立文件'))

            # f) 技术债标记
            for i, l in enumerate(lines, 1):
                if re.search(r'\b(TODO|FIXME|HACK|XXX)\b', l):
                    problems.append((rel, i, f'遗留技术债标记: {l.strip()[:56]}'))

            # g) 行内注释块不超过 MAX_INLINE_COMMENT_LINES 行
            #
            # 判据：注释写"为什么"，一两句就够；写到 5 行以上基本是
            # 把使用手册或架构说明塞进了注释——那些内容属于 README/AGENTS/DEVELOPMENT。
            # 实测阈值：238 个行内注释块里 227 个（95%）≤3 行，自然断点在 4 行，
            # 超过 4 行的 5 块全部是"文档型"注释。
            i = 0
            while i < len(lines):
                s = lines[i].strip()
                if s.startswith('//') and not s.startswith('///'):
                    j = i
                    while (j < len(lines) and lines[j].strip().startswith('//')
                           and not lines[j].strip().startswith('///')):
                        j += 1
                    body = [lines[k].strip().lstrip('/').strip() for k in range(i, j)]
                    meaningful = [x for x in body if x]
                    # 纯分隔线（///// 或全横线）不算注释内容，单独由别的规则管
                    if (len(body) > MAX_INLINE_COMMENT_LINES and meaningful
                            and not all(re.fullmatch(r'[-=/]+', x) for x in meaningful)):
                        problems.append((rel, i + 1,
                                         f'行内注释块 {len(body)} 行，超过 '
                                         f'{MAX_INLINE_COMMENT_LINES} 行上限'
                                         f'（说明请写进 README/AGENTS/DEVELOPMENT）'))
                    i = j
                else:
                    i += 1

    # e) region 缩进一致（只在非测试项目强制，测试夹具常有层级）
    for dirpath, _, files in os.walk(project_root):
        if os.sep + 'bin' in dirpath or os.sep + 'obj' in dirpath:
            continue
        for f in sorted(files):
            if not f.endswith('.cs'):
                continue
            rel = os.path.relpath(os.path.join(dirpath, f), project_root)
            text = read(os.path.join(dirpath, f))
            stack = []
            for i, l in enumerate(text.split('\n'), 1):
                m = re.match(r'^(\s*)#(region|endregion)\b', l)
                if not m:
                    continue
                indent, kind = m.group(1), m.group(2)
                if kind == 'region':
                    stack.append((indent, i))
                elif stack:
                    open_indent, open_line = stack.pop()
                    if open_indent != indent:
                        problems.append((rel, i,
                                         f'#endregion 缩进({len(indent)})与 '
                                         f'#region({len(open_indent)}, 第 {open_line} 行)不一致'))
            for open_indent, open_line in stack:
                problems.append((rel, open_line, '#region 没有对应的 #endregion'))

    # g) csproj SDK
    #
    # 库项目必须是 Microsoft.NET.Sdk。Web 宿主合法地需要 Microsoft.NET.Sdk.Web，
    # 但必须显式登记在 WEB_SDK_ALLOWED 里——否则"库被误写成 .Web"这类事故
    # （曾真实发生过）就查不出来了。
    for dirpath, _, files in os.walk(project_root):
        if os.sep + 'bin' in dirpath or os.sep + 'obj' in dirpath:
            continue
        for f in sorted(files):
            if not f.endswith('.csproj'):
                continue
            rel = os.path.relpath(os.path.join(dirpath, f), project_root)
            text = read(os.path.join(dirpath, f))

            m = re.search(r'<Project\s+Sdk="([^"]+)"', text)
            if not m:
                continue
            sdk = m.group(1)
            if sdk == 'Microsoft.NET.Sdk':
                continue
            if sdk == 'Microsoft.NET.Sdk.Web' and project_name in WEB_SDK_ALLOWED:
                continue
            hint = (f'（若确为 Web 宿主，请把 {project_name} 加入 WEB_SDK_ALLOWED）'
                    if sdk == 'Microsoft.NET.Sdk.Web' else '')
            problems.append((rel, 1, f'csproj 的 SDK 不应是 {sdk}{hint}'))

    return problems


def check_build_file_comments(root, label):
    """
    构建文件（csproj / props / targets）里不许有注释。

    说明应当进 README/DEVELOPMENT：写在构建文件里必然与文档重复
    （实测 9 处 csproj 注释里最长的一处几乎是 AGENTS.md §4 的逐字复述，
    另有一段在两个 csproj 里逐字重复）。

    仓库根也要查——`Directory.Build.props` 不在任何 project 目录下。
    """
    problems = []
    for dirpath, dirnames, files in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in ('bin', 'obj')]
        for f in sorted(files):
            if not f.endswith(('.csproj', '.props', '.targets')):
                continue
            path = os.path.join(dirpath, f)
            text = read(path)
            rel = os.path.relpath(path, REPO_ROOT)
            for m in re.finditer(r'<!--', text):
                problems.append((label, rel, text[:m.start()].count('\n') + 1))
    return problems


def check_too_fine(project_root):
    fine = []
    for dirpath, _, files in os.walk(project_root):
        if os.sep + 'bin' in dirpath or os.sep + 'obj' in dirpath:
            continue
        cs = [f for f in files if f.endswith('.cs')]
        rel = os.path.relpath(dirpath, project_root)
        if rel == '.' or len(cs) != 1:
            continue
        fine.append((rel, cs[0]))
    return fine


def main():
    total = 0

    # 构建文件禁止注释：先查仓库根（Directory.Build.props 等不在任何 project 下）
    build_comments = check_build_file_comments(REPO_ROOT, '(repository root)')
    project_roots = [os.path.join(REPO_ROOT, p) for p in PROJECTS
                     if os.path.isdir(os.path.join(REPO_ROOT, p))]
    for root in project_roots:
        build_comments += check_build_file_comments(root, os.path.basename(root))

    for project in PROJECTS:
        project_root = os.path.join(REPO_ROOT, project)
        if not os.path.isdir(project_root):
            continue

        rows = scan(project_root)
        print('=' * 70)
        print(f'{project}  ({len(rows)} 个 .cs)')
        print('=' * 70)

        ns_problems = check_namespace(project, rows)
        print(f'[1] 命名空间与文件夹不符: {len(ns_problems)} 处')
        for rel, expected, actual in ns_problems:
            print(f'      {rel}: 期望 {expected} / 实际 {actual}')

        purity = check_purity(project, rows)
        print(f'[2] public 契约与 internal 实现混放: {len(purity)} 处')
        for d, pub, internal in purity:
            print(f'      {d}/')
            print(f'          public  : {", ".join(pub)}')
            print(f'          internal: {", ".join(internal)}')

        cap_purity = check_capability_purity(project_root)
        print(f'[3] Interfaces/ 与 Models/ 内容不纯: {len(cap_purity)} 处')
        for rel, detail in cap_purity:
            print(f'      {rel}: {detail}')

        fine = check_too_fine(project_root)
        print(f'[4] 只有一个文件的文件夹: {len(fine)} 处')
        for rel, f in fine:
            print(f'      {rel}/  ({f})')

        style = check_style(project, project_root)
        print(f'[5] 编码约定违规: {len(style)} 处')
        for rel, ln, detail in style:
            print(f'      {rel}:{ln}  {detail}')

        print()
        total += len(ns_problems) + len(purity) + len(cap_purity) + len(style)

    print('=' * 70)
    print(f'[6] 构建文件里的注释: {len(build_comments)} 处')
    for label, rel, ln in build_comments:
        print(f'      {rel}:{ln}  （{label}）构建文件里不要有注释，说明写进 README/DEVELOPMENT')
    total += len(build_comments)

    if total:
        print(f'结构审查未通过：{total} 处问题需要处理')
        return 1

    print('结构审查通过：命名空间一致、文件夹性质单一、编码约定合规')
    return 0


if __name__ == '__main__':
    sys.exit(main())
