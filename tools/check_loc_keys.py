"""文案键完整性检查 —— AmbientLight 的文案守护网 (C#/XAML 引用 ⇄ 字典 ⇄ 模式表 ⇄ 语言表)。

用法 (任意目录都行, 路径按本文件位置推导):
    python tools/check_loc_keys.py     # 退出码 0 = 全过; 非 0 = 有问题 (看看上面哪一行不是 ✓)

**设计原则: 能反推的绝不写死** —— 字典清单是扫出来的, 模式号从 `ModeCatalog.Modes` 反推,
键命名域从字典键推导, "多段文案"从各字典的共识推导。所以:
    · **加一种语言** → 放字典文件 + 在 `LanguageService.All` 登记一行, 本脚本**不用改**;
      唯一的额外动作 = 若该语言本身用汉字, 把它登记进下面的 `CJK_KEYS` (否则汉字残留会误报)。
    · **加一款固件特效** → 加进 `ModeCatalog.Modes`, 本脚本会自动把它纳入"文案齐备/磁贴对账"。

检查项:
   ① 正向: C# 里 "域.xxx" 字面量 + XAML 里 {DynamicResource 域.xxx} 是否都在字典里
      (域名 = 从字典键推导, 不写死清单);
   ② 反向: 字典里有没有谁都没引用的键 (Metric.* 视为整命名空间动态引用, 逐键交给探针);
   ③ 模式: ModeCatalog.Modes 里每个号都要有 Mode.Name.<id> (磁贴行还要 Home.Fx.Tip.<id>);
      磁贴 tooltip 必须以同名模式名开头 (改名漏改 tooltip 会现形);
   ④ XAML 不留中文字面量 (属性值/文本节点; 注释里可以有);
   ⑤ 字典文件 ⇄ LanguageService.All 双向一致 (社区加语言最容易漏的一步);
   ⑥ 换行编码: 值里用 &#10; 的元素必须带 xml:space="preserve" (否则 XAML 会把换行归一成空格);
   ⑦ "多段文案"断行: 各字典里**过半**的语言都断行的键, 必须在所有语言里都断行
      (键集从字典共识推导 —— 新增多段文案自动纳入, 不用回来补清单);
   ⑧ 所有字典键集**逐键一致** (一本有一本无 = 切语言时那一行静默显示 key 原文);
   ⑨ "忘了翻译": 任意两本字典的值不能完全相同 (整本复制粘贴没翻); 非汉字系语言里不许有汉字
      (漏译几行会被抓)。
"""
import io, re, pathlib, sys
from itertools import combinations

REPO = pathlib.Path(__file__).resolve().parent.parent      # tools/ 的上一级 = 仓库根
ROOT = REPO / 'managed' / 'AmbientLight'
LOC = ROOT / 'Localization'

# ---- 字典清单: 扫目录, 不写死语言 ----------------------------------------
dict_paths = sorted(LOC.glob('Strings.*.xaml'))
if not dict_paths:
    print(f'!! 没找到任何 {LOC}/Strings.*.xaml')
    sys.exit(1)

def parse_dict(path):
    """所有字典结构必须完全同构 → 用同一份正则。"""
    text = io.open(path, encoding='utf-8').read()
    k = set(re.findall(r'x:Key="([^"]+)"', text))
    v = dict(re.findall(r'x:Key="([^"]+)"[^>]*>(.*?)</sys:String>', text))
    return text, k, v

dicts = {p.name: parse_dict(p) for p in dict_paths}
for name, (_, k, _v) in dicts.items():
    print(f'字典键数 ({name}): {len(k)}')

base_name = dict_paths[0].name                       # 对账基准 (任意一本都行: 键集必须全等)
base_text, keys, values = dicts[base_name]

# ---- 键命名域: 从字典键的第一段推导 (以前是写死的元组 → 加新域会静默漏检) ----
DOMAINS = tuple(sorted({k.split('.')[0] for k in keys if '.' in k}))
print(f'键命名域 (从字典键推导, {len(DOMAINS)} 个): {"/".join(DOMAINS)}')

# ---- 模式号: 从代码反推 (ModeCatalog 的模式表), 不写死范围 -------------------
# 模式表长这样:  public static readonly ModeDef[] Modes = [ new(0, ...), new(1, ...) ];
# 所以按 `new(<id>,` 抠号 —— 表是唯一事实源, 兜底行 Unknown 在数组之外, 天然不会混进来。
modecat = ''
for p in sorted(ROOT.rglob('ModeCatalog.cs')):
    if 'obj' not in p.parts and 'bin' not in p.parts:
        modecat = io.open(p, encoding='utf-8').read()
mode_nums = []
_m = re.search(r'\bModes\s*=\s*\[(.*?)\n\s*\];', modecat, re.S)
if _m:
    mode_nums = sorted({int(x) for x in re.findall(r'new\(\s*(-?\d+)\s*,', _m.group(1))})
else:
    _m2 = re.search(r'\bAll\s*=\s*\[([^\]]*)\]', modecat)      # 兼容老写法 (All 是字面数组)
    mode_nums = sorted({int(x) for x in re.findall(r'-?\d+', _m2.group(1))}) if _m2 else []
print(f'模式号 (从模式表反推): {mode_nums or "没解析到 !! "}')

# ---- ① 正向: 代码/XAML 引用是否都在字典里 --------------------------------
pat = re.compile(r'"((?:' + '|'.join(DOMAINS) + r')\.[A-Za-z0-9._]+)"')
refs = set()
for p in sorted(ROOT.rglob('*.cs')):
    if 'obj' in p.parts or 'bin' in p.parts:
        continue
    text = io.open(p, encoding='utf-8').read()
    # 去注释, 免得注释里的示例字样误报
    text = re.sub(r'//[^\n]*', '', text)
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    for m in pat.finditer(text):
        refs.add(m.group(1))

# XAML 侧: {DynamicResource 键} (只认既定域, 主题刷子等其它资源键不算)
xaml_ref_pat = re.compile(r'\{DynamicResource\s+((?:' + '|'.join(DOMAINS) + r')\.[A-Za-z0-9._]+)\s*\}')
for p in sorted(ROOT.rglob('*.xaml')):
    if 'obj' in p.parts or 'bin' in p.parts or p.name.startswith('Strings.'):
        continue
    text = io.open(p, encoding='utf-8-sig').read()
    for m in xaml_ref_pat.finditer(text):
        refs.add(m.group(1))

clean = {k for k in refs if not k.endswith('.')}          # 去掉 "Metric." / "Mode.Name." 这类前缀片段
missing = sorted(k for k in clean if k not in keys)
print('代码/XAML 引用(静态)缺失:', '无 ✓' if not missing else missing)

# ---- ② 反向: 未被引用的键 ------------------------------------------------
dyn = {
    'Mode.Name.': [f'Mode.Name.{n}' for n in mode_nums],
    # Metric.* 是**整命名空间**动态引用 (`Loc.T("Metric." + key)`) → 一律算"已引用", 不逐键猜；
    # "字典的 Metric.* 与 MetricKeysForMode 的并集一致"由探针跑真函数对账 (脚本读不到函数返回值)。
    'Metric.': sorted(k for k in keys if k.startswith('Metric.')),
}
dyn_missing = []
for pref, expanded in dyn.items():
    dyn_missing += [k for k in expanded if k not in keys]
print('动态键缺失:', '无 ✓' if not dyn_missing else dyn_missing)

used = set(clean)
for pref, expanded in dyn.items():
    used.update(expanded)
unused = sorted(k for k in keys if k not in used)
print('字典未引用:', '无 ✓' if not unused else unused)

# ---- ④ XAML 残留中文字面量 (属性值 / 文本节点; 注释不算) -------------------
han = re.compile(r'[\u4e00-\u9fff]')
attr_pat = re.compile(r'[A-Za-z][\w:.]*="[^"]*[\u4e00-\u9fff][^"]*"')
leftover = []
for p in sorted(ROOT.rglob('*.xaml')):
    if 'obj' in p.parts or 'bin' in p.parts or p.name.startswith('Strings.'):
        continue
    text = io.open(p, encoding='utf-8-sig').read()
    text_nc = re.sub(r'<!--.*?-->', lambda m: '\n' * m.group(0).count('\n'), text, flags=re.S)
    for m in attr_pat.finditer(text_nc):
        line = text_nc[:m.start()].count('\n') + 1
        leftover.append(f'{p.name}:{line} {m.group(0)[:60]}')
    for m in re.finditer(r'>([^<>]*[\u4e00-\u9fff][^<>]*)<', text_nc):
        line = text_nc[:m.start()].count('\n') + 1
        leftover.append(f'{p.name}:{line} (文本节点) {m.group(1).strip()[:40]}')
print('XAML 残留中文字面量:', '无 ✓' if not leftover else leftover)

# ---- ③ 模式文案齐备 + 磁贴 tooltip 对账 -----------------------------------
# 磁贴模式号 = 从字典里的 Home.Fx.Tip.<n> 反推 (加磁贴自动纳入)。
tip_nums = sorted({int(k.rsplit('.', 1)[1]) for k in keys if k.startswith('Home.Fx.Tip.')})
print(f'固件磁贴模式号 (从 Home.Fx.Tip.* 反推): {tip_nums or "没有 !! "}')
drift = []
for name, (_t, _k, v) in dicts.items():
    drift += [f'{name}:{i} tooltip 前缀 != 模式名' for i in tip_nums
              if v.get(f'Home.Fx.Tip.{i}', '').split(' · ')[0] != v.get(f'Mode.Name.{i}', '')]
    drift += [f'{name}:缺 Mode.Name.{i}' for i in tip_nums if f'Mode.Name.{i}' not in _k]
print('磁贴 tooltip 与模式名对账:', '无漂移 ✓' if not drift else drift)

mode_missing = []
for name, (_t, k, _v) in dicts.items():
    mode_missing += [f'{name}:缺 Mode.Name.{n}' for n in mode_nums if f'Mode.Name.{n}' not in k]
print(f'模式表里 {len(mode_nums)} 个模式号都有文案:', '无 ✓' if not mode_missing else mode_missing)

# ---- ⑤ 字典文件 ⇄ LanguageService.All 双向一致 ----------------------------
# 社区加语言最容易漏的一步: 文件放了却忘了在语言表登记 (界面里根本选不到它)。
langsvc = ''
for p in sorted((ROOT / 'Services').rglob('LanguageService.cs')):
    langsvc = io.open(p, encoding='utf-8').read()
# ⚠️ 必须先去注释再抠: `All` 表里就有一条"将来加繁体"的**示例行**写在注释里
# (第一次跑这条检查时它把示例行当成了真登记 → 误报"有文件未登记/登记了没文件")。
langsvc_code = re.sub(r'//[^\n]*', '', langsvc)
langsvc_code = re.sub(r'/\*.*?\*/', '', langsvc_code, flags=re.S)
registered = set(re.findall(r'new\(\s*LangChoice\.\w+\s*,\s*"([^"]+)"\s*,', langsvc_code))
file_keys = set()
for name in dicts:
    m = re.match(r'Strings\.(.+)\.xaml', name)
    if m:
        file_keys.add(m.group(1))
only_file = sorted(file_keys - registered)
only_table = sorted(registered - file_keys)
print(f'语言表 (LanguageService.All) 登记 {len(registered)} 种: {sorted(registered) or "没解析到 !!"}')
print('字典文件 ⇄ 语言表登记:', '双向一致 ✓' if not only_file and not only_table else
      f'有文件未登记 {only_file} / 登记了没文件 {only_table}')

# ---- ⑥ 换行编码: 值里用 &#10; 的元素必须带 xml:space="preserve" -------------
# (实测: 不带的话 XAML 会把 &#10; 归一化成空格 —— 换行静默失效; 工具 .workbuddy/tmp/nl-probe)
elem_pat = re.compile(r'<sys:String x:Key="([^"]+)"([^>]*)>(.*?)</sys:String>', re.S)
no_preserve = []
for name, (text, _k, _v) in dicts.items():
    for m in elem_pat.finditer(text):
        if '&#10;' in m.group(3) and 'xml:space="preserve"' not in m.group(2):
            no_preserve.append(f'{name}:{m.group(1)}')
print('换行编码 (&#10; 必须配 xml:space=preserve):', '无 ✓' if not no_preserve else no_preserve)

# ---- ⑦ "多段文案"的断行必须在所有语言里都保留 ------------------------------
# 判据 = **从字典共识推导**: 过半的语言在这个键上断了行 → 它是一条多段文案 → 所有语言都要断行。
# (以前这里是一份写死的键清单 —— 新增多段文案时清单不会自己长, 检查静默失效。)
raw_by_dict = {name: {m.group(1): m.group(3) for m in elem_pat.finditer(text)}
               for name, (text, _k, _v) in dicts.items()}
multi_by_key = {k: sum(1 for raw in raw_by_dict.values() if '&#10;' in raw.get(k, ''))
                for k in keys}
multiline_keys = sorted(k for k, n in multi_by_key.items() if n * 2 > len(dicts))
nl_lost = []
for name, raw in raw_by_dict.items():
    nl_lost += [f'{name}:{k}' for k in multiline_keys if '&#10;' not in raw.get(k, '')]
print(f'多段文案断行齐备 ({len(multiline_keys)} 条, 由字典共识推导):',
      '无 ✓' if not nl_lost else nl_lost)

# ---- ⑧ 所有字典键集逐键一致 (缺键 = 切语言时那一行变成 key 原文, 静默) ------
parity_bad = []
for name, (_t, k, _v) in dicts.items():
    if name == base_name:
        continue
    only_base = sorted(keys - k)
    only_this = sorted(k - keys)
    if only_base or only_this:
        parity_bad.append(f'{name}: 缺 {only_base} / 多 {only_this}')
print(f'{len(dicts)} 本字典键集一致:', '逐键一致 ✓' if not parity_bad else parity_bad)

# ---- ⑨ "忘了翻译"检测 -----------------------------------------------------
# ① 任意两本不能逐值相同 (= 整本复制粘贴没翻);
# ② 一本字典若**不该出现汉字**, 就不该有 —— 判"该不该"用两种依据 (任一成立即豁免):
#    · 显式名单 CJK_KEYS (= 该语言本身就用汉字/汉字系文字) —— **按前缀匹配**,
#      所以 zh 这一项就覆盖了将来的 Strings.zh-TW.xaml / Strings.zh-HK.xaml;
#      加语言时若它用到 CJK 字符, 把 key 加进这份名单 —— 否则会误报 (演练时 ja 的「閉じる」被抓过, 实测);
#    · 含汉字的键占比 ≥ 20% (= 这本字典整体就是汉字语言, 名单漏登记时的兜底)。
CJK_KEYS = {'zh', 'ja'}
copy_bad = []
for (na, (_ta, _ka, va)), (nb, (_tb, _kb, vb)) in combinations(dicts.items(), 2):
    if va and va == vb:
        copy_bad.append(f'{na} == {nb}')
stale = []
for name, (text, _k, v) in dicts.items():
    lang_key = re.match(r'Strings\.(.+)\.xaml', name)
    lang_key = lang_key.group(1) if lang_key else name
    ratio = (sum(1 for x in v.values() if han.search(x)) / len(v)) if v else 0
    if any(lang_key == k or lang_key.startswith(k + '-') for k in CJK_KEYS) or ratio >= 0.20:
        continue                                   # 汉字系语言 → 豁免
    nc = re.sub(r'<!--.*?-->', '', text, flags=re.S)
    bad = [m.group(1) for m in elem_pat.finditer(nc) if han.search(m.group(3))]
    if bad:
        stale.append(f'{name}: {bad[:6]}{" …" if len(bad) > 6 else ""} ({len(bad)} 条)')
print('字典即副本 (逐值相同):', '无 ✓' if not copy_bad else copy_bad)
print('字典残留汉字 (非汉字系语言):', '无 ✓' if not stale else stale)

ok = not (missing or dyn_missing or unused or leftover or drift or no_preserve
          or mode_missing or mode_nums == [] or only_file or only_table
          or nl_lost or parity_bad or copy_bad or stale)
print('检查通过 ✓' if ok else '存在问题 !!')
sys.exit(0 if ok else 1)
