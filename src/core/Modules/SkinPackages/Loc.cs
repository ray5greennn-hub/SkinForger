using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// Chinese interface for SkinForger (Settings page, 2026-11-16). The texts are translated as they are drawn: prefixes
/// on the IMGUI calls that take a caption (GUILayout / GUI Label, Button, Toggle, Box, RepeatButton; never text
/// fields, so typed text is untouched), active only while a SkinForger window is drawing (creator window incl. Main's
/// Image page, Skin Library, Preview). Exact texts first, then fixed prefixes, then patterns for texts with numbers.
/// Untranslated texts stay English.
/// </summary>
internal static class Loc
{
    private const string Prefix = "[SkinForge][Loc]";
    [ThreadStatic] private static int _depth;
    internal static bool Active => _depth > 0 && SfSettings.Chinese;
    /// <summary>True while a SkinForger window is drawing, whatever the language.</summary>
    internal static bool Drawing => _depth > 0;
    internal static void Enter() => _depth++;
    internal static void Exit() { if (_depth > 0) _depth--; }

    /// <summary>
    /// Relabelling, applied in EVERY language (unlike the translations below, which need Chinese to be on).
    /// "Root" is upstream SkinChanger's word for "the images in SkinCreator, as edited" and says nothing to a
    /// player; the authored Main will be built with the new label, and until then the old Main's caption is
    /// replaced as it is drawn. A button's behaviour is unaffected: Main matches its buttons by position.
    /// </summary>
    private static readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal)
    {
        ["Root"] = "Edited",
        // 2026-11-19: the last three labels SkinForger still carried word-for-word from upstream
        // SkinChanger. The behaviour behind them was always SkinForger's own; only the wording was
        // inherited, and wording is the one kind of upstream expression that rewriting the code does not
        // remove. The authored Main is built with the new words; the old Main's are replaced as they are
        // drawn, so both show the same thing. Each one now says what it does.
        // 2026-11-20: "Reload" is back, by the user's word. It is the name they and the skin creators use.
        ["Deselect All"] = "Clear Selection",
        ["Reset Window"] = "Reset Position",
    };

    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
    {
        // tabs / settings
        ["Image"] = "图片", ["Text"] = "文字", ["Export Skin Package"] = "导出皮肤包", ["Settings"] = "设置",
        // Main: Image page
        ["Characters"] = "角色", ["Elfins"] = "小精灵", ["Original"] = "原版", ["Dump"] = "导出", ["Dump All"] = "全部导出",
        // "Edited" is the renamed "Root" (see Renames): the Chinese label follows the new name.
        ["Reload"] = "重新加载", ["Edited"] = "编辑版", ["Dump Cursor"] = "导出光标", ["Dump Other"] = "导出其他",
        ["Do not enlarge the canvas: a cursor's position is tied to its hotspot, so a bigger picture makes clicks land wrong."]
            = "不建议扩框：光标的位置和鼠标热点绑定，画布变大会让点击错位、手感出错。", ["Clear Selection"] = "清空选择", ["Reset Position"] = "重置位置", ["Preview"] = "预览", ["Icon"] = "图标",
        // Preview
        ["Character"] = "角色", ["Context"] = "场景", ["Animation"] = "动画", ["Skin"] = "立绘", ["Frame"] = "逐帧",
        ["Play"] = "播放", ["Pause"] = "暂停", ["Restart"] = "重新开始", ["Loop"] = "循环", ["Speed"] = "速度", ["Close"] = "关闭",
        ["Preview unavailable"] = "无法预览", ["Form"] = "形态", ["►  Play"] = "►  播放", ["||  Pause"] = "||  暂停",
        ["< > step one image (pauses)"] = "< > 逐张切换（会暂停）",
        // Skin Library
        ["SKIN LIBRARY"] = "皮肤库", ["Press 2 to close"] = "按 2 关闭", ["Active Skins"] = "已启用", ["Installed Skins"] = "已安装的皮肤",
        ["Open Skins Folder"] = "打开皮肤文件夹", ["Refresh"] = "刷新", ["Turn All Off"] = "全部关闭", ["ON"] = "开", ["OFF"] = "关",
        ["Details"] = "详情", ["Hide Details"] = "收起详情", ["Description"] = "介绍", ["Changes"] = "修改内容", ["Conflicts"] = "冲突",
        ["No conflict"] = "无冲突", ["No conflict with your other enabled skins."] = "和你启用的其他皮肤没有冲突。",
        ["No skins installed yet."] = "还没有安装皮肤。", ["Install"] = "安装", ["Update"] = "更新", ["Cancel"] = "取消",
        ["Turn On Anyway"] = "仍然开启", ["Skin Conflict Detected"] = "检测到皮肤冲突",
        ["Your Exported Skins (not installed)"] = "你导出的皮肤（未安装）",
        ["Install copies the file from SkinForger/ExportSkin to SkinForger/Skins."] = "安装会把文件从 SkinForger/ExportSkin 复制到 SkinForger/Skins。",
        ["Skins you export yourself are listed below with an Install button."] = "你自己导出的皮肤列在下面，可以点安装。",
        ["Put .sfskin files into the SkinForger/Skins folder (\"Open Skins Folder\" above). They show up here automatically."] = "把 .sfskin 文件放进 SkinForger/Skins 文件夹（点上面的“打开皮肤文件夹”），会自动显示在这里。",
        ["This file is damaged or not a SkinForger skin."] = "这个文件已损坏，或不是 SkinForger 皮肤。",
        ["Loading order:  SkinCreator  >  newest skin turned on  >  older skins  >  Muse Dash"] = "加载顺序：SkinCreator > 最新开启的皮肤 > 较早的皮肤 > Muse Dash 原版",
        ["Your SkinCreator work stays unchanged and on top."] = "你在 SkinCreator 里的作品不受影响。",
        ["Your own work in SkinCreator always stays on top."] = "你在 SkinCreator 里的作品始终优先。",
        // Text page
        ["Language"] = "语言", ["Search"] = "搜索", ["Show"] = "显示", ["Edited only"] = "只看改过的", ["Dump Text"] = "导出文字",
        ["Open Text Folder"] = "打开文字文件夹", ["All"] = "全部", ["Names"] = "名字", ["Descriptions"] = "介绍", ["Skills"] = "技能",
        ["CV"] = "声优", ["Dialogues"] = "台词", ["Loading Tips"] = "加载提示", ["Fonts"] = "字体", ["Open Fonts Folder"] = "打开字体文件夹",
        ["Show Font File"] = "显示字体文件", ["Original "] = "原文", ["Your text"] = "你的文字", ["Save"] = "保存", ["Revert"] = "撤销",
        ["Use Original"] = "改回原文", ["Not saved"] = "未保存", ["● Edited"] = "● 已修改",
        ["Select a text on the left to edit it."] = "在左边选择一条文字进行编辑。",
        ["A .ttf / .otf in Text/Fonts/CN or Text/Fonts/EN replaces the font of the whole game in that language."] = "放进 Text/Fonts/CN 或 Text/Fonts/EN 的 .ttf / .otf 字体会替换该语言下整个游戏的字体。",
        // Export page
        ["Available Skins"] = "可导出的皮肤", ["Select All"] = "全选", ["Clear All"] = "全部取消", ["Also Include"] = "一并导出",
        ["Package Name"] = "皮肤包名字", ["Author"] = "作者", ["Open ExportSkin Folder"] = "打开 ExportSkin 文件夹",
        ["Checking skin folders..."] = "正在检查皮肤文件夹……", ["Output location:  SkinForger/ExportSkin/"] = "保存位置：SkinForger/ExportSkin/",
        ["(optional, leave empty to stay anonymous)"] = "（可不填，留空即匿名）", ["(optional, shown in players' Skin Library)"] = "（可不填，会显示在玩家的皮肤库里）",
        ["  (a number is added if that name already exists)"] = "  （重名时会自动加上编号）",
        ["Select skin folders to include in your package."] = "选择要打进皮肤包的皮肤文件夹。",
        ["Select at least one Ready skin folder, Text, Fonts or Loading Pictures."] = "至少选择一个可导出的皮肤文件夹、文字、字体或加载图。",
        ["Only the images you changed are packed. Every part you did not change shows each player's own game art."] = "只会打包你改过的图片。没改的部分会显示玩家自己游戏里的原图。",
        ["Preview picture:  SkinCreator/preview.png will be included"] = "预览图：会包含 SkinCreator/preview.png",
        ["Ready"] = "可导出", ["Missing Files"] = "缺少文件", ["Broken Files"] = "文件损坏", ["No Edits"] = "没有改动",
        ["Loading Pictures"] = "加载图", ["Elfin"] = "小精灵",
        ["Choose what to include: characters, elfins, texts and custom loading pictures."] = "选择要打包的内容：角色、小精灵、文字、自定义加载图。",
        ["Festival"] = "节日台词", ["Sound"] = "声音", ["Sounds"] = "声音", ["Voices & BGM"] = "语音和 BGM", ["Stop"] = "停止", ["Open Sounds Folder"] = "打开声音文件夹",
        ["Mute All"] = "全部静音", ["Unmute All"] = "全部取消静音", ["Open Folder"] = "打开文件夹", ["▶ Original"] = "▶ 原版", ["▶ Yours"] = "▶ 替换版", ["|| Original"] = "|| 原版", ["|| Yours"] = "|| 替换版",
        [" Mute"] = " 静音", ["Muted"] = "已静音", ["Replaced"] = "已替换", ["Skin Library"] = "皮肤库",
        ["Choose a character on the left."] = "在左边选择一个角色。", ["Reading the game's sound list..."] = "正在读取游戏的声音列表……",
        ["No replaced or muted sounds yet (Sound page)"] = "还没有替换或静音的声音（声音页）",
        ["Click a name to copy it, then put  <Name>.wav / .ogg / .mp3  into  SkinCreator/Sounds/<Character>/  and click Reload."] = "点名字复制，然后把  <名字>.wav / .ogg / .mp3  放进  SkinCreator/Sounds/<角色>/ ，再点重新加载。",
        ["Apply  (character chosen)"] = "选择角色", ["Start  (song starts)"] = "开始游戏", ["Main  (touched on the main menu)"] = "主界面互动",
        ["Hidden  (hidden main-menu lines)"] = "隐藏互动", ["Festival  (holidays)"] = "节日", ["Hurt"] = "受伤", ["Hurt  (in a song)"] = "受伤（打歌中）", ["Empty hit  (in a song)"] = "空挥（打歌中）",
        ["Edit in Text page"] = "去文字页改", ["Original voice"] = "原版语音",
        ["Recently Played"] = "最近播放", ["Nothing played yet."] = "还没有播放过声音。",
        ["Every sound the game played since it started, newest first (play a song to see hit / weapon sounds)."] = "游戏启动以来播放过的所有声音，最新的在最上面（打一首歌就能看到打击 / 武器音效）。", ["Lost  (song failed)"] = "失败",
        ["Victory  (song cleared)"] = "胜利", ["Transform"] = "变身", ["Character BGM"] = "角色 BGM", ["Other"] = "其他",
        ["Custom Loading Pictures"] = "自定义加载图", ["Edited texts"] = "改过的文字", ["Loading pictures"] = "加载图",
        ["Other Pictures"] = "其他贴图", ["Other pictures"] = "其他贴图",
        ["What to pack"] = "打包范围",
        ["  Pack every image, not only the ones I changed"] = "  打包全部贴图，不只是我改过的",
        ["Ticked (default):  every PNG and sidecar in the chosen folders travels, the selection\nthumbnail included. Bigger, and certain — nothing can be left behind by mistake."]
            = "勾选（默认）：选中文件夹里的每一张 PNG 和它的 json 都会带上，包括选择界面的封面图。\n包更大，但确定——不会漏掉任何部件。",
        ["Only the images SkinForger believes you changed. Smaller, but it is a GUESS: a part you\nredrew at its original size can look untouched, and then it is missing for everyone else."]
            = "只打包 SkinForger 判断你改过的贴图。包更小，但那是「判断」：\n按原尺寸重画的部件看起来像没动过，于是在别人那里就是缺的。",
        ["No pictures in SkinCreator/Images/Other (dump them from the Image page, Pictures on screen)"]
            = "SkinCreator/Images/Other 里没有图片（到图片页的「屏幕上出现过的图片」里导出）",
        ["No elfin dumped yet (Image page, Elfins list)."] = "还没有导出小精灵（图片页的小精灵列表）。",
        ["No character dumped yet (Image page, Characters list)."] = "还没有导出角色（图片页的角色列表）。",
        ["No edited texts yet (Text page)"] = "还没有改过的文字（文字页）",
        ["No font files in SkinCreator/Text/Fonts"] = "SkinCreator/Text/Fonts 里没有字体文件",
        ["No custom loading pictures in SkinCreator/CustomLodingPic"] = "SkinCreator/CustomLodingPic 里没有自定义加载图",
        // Image page footer: the cursor dump and the history of pictures the game has shown (2026-11-18)
        ["Clear"] = "清空", ["Show:"] = "显示：", ["Find:"] = "查找：",
        [" Characters & monsters"] = " 角色和怪物", [" Stage art"] = " 场景美术", [" HUD"] = " 界面",
        ["character"] = "角色", ["stage"] = "场景", ["HUD"] = "界面",
        ["Nothing seen yet. Play a song (or open any screen) and the pictures it draws land here."]
            = "还没有看到任何图。打一首歌（或者打开任意界面），它画出来的图就会出现在这里。",
        ["Nothing matches the filter."] = "没有符合筛选条件的内容。",
        ["Newest first. Dump writes one picture to SkinCreator\\Images\\Other and never overwrites a file you already edited."]
            = "最新的在最上面。导出会把这一张图写到 SkinCreator\\Images\\Other，已经存在的文件不会被覆盖。",
        // Skin Library / export
        ["Press 0 to close"] = "按 0 关闭", ["Sharing"] = "分享",
        // Skin Library (2026-11-18: the library got its own Settings button, so players who never open
        // the creator window can still switch the language)
        ["Back"] = "返回", ["Applying..."] = "正在应用……",
        ["All skins turned off."] = "已关闭全部皮肤。",
        ["1 Installed"] = "已安装 1 个",
        // Text page: the label of a single entry (TextStore.LabelOf) — these are shown on every row, so
        // leaving them out left the page looking untranslated even though its headings were done.
        ["Name"] = "名字", ["Costume Name"] = "服装名", ["Skin Name"] = "皮肤名",
        ["Description"] = "介绍", ["Card Name"] = "卡片名", ["Card Description"] = "卡片介绍",
        ["Skill"] = "技能", ["Thief Skill"] = "偷取技能",
        ["Dialogue"] = "台词", ["Idle Dialogue"] = "待机台词", ["Review Dialogue"] = "评价台词",
        ["Cat Dialogue"] = "猫化台词", ["April Fools Dialogue"] = "愚人节台词",
        ["Loading Tip"] = "加载提示",
        // Export page: per-character ticks (2026-11-19)
        ["texts"] = "条文字", ["files"] = "个文件",
        [" On screen now"] = " 只看当前画面",
        ["— not loaded; open that screen again"] = "— 已卸载，回到那个界面再试",
        ["Newest first. A dimmed name is not on screen right now; it can usually still be dumped, and only turns grey-red once a Dump has found it unloaded. Pictures land in SkinCreator\\Images\\Other and nothing you have edited is overwritten."]
            = "最新的在最上面。变灰的名字表示它此刻不在屏幕上，通常仍然能导出；只有导出时发现它已被卸载，才会变成暗红。图片写入 SkinCreator\\Images\\Other，不会覆盖你改过的文件。",
        // Dump by name (2026-11-19)
        ["By name:"] = "按名字：", ["Load & Dump"] = "加载并导出",
        ["Startup warning"] = "启动警告页", ["Headphone screen"] = "耳机提示页",
        ["Type the name of a prefab or a picture first."] = "先填一个预制体或贴图的名字。",
        ["The game's resource manager is not ready yet."] = "游戏的资源管理器还没准备好。",
        // Settings page
        ["Hotkeys"] = "快捷键",
        ["Select at least one Ready character or elfin, edited texts, fonts or loading pictures."] = "至少选择一个可导出的角色或小精灵、改过的文字、字体或加载图。",
        // Readme (2026-11-21): the creator's own notes travel with the package and the player reads them here.
        ["Readme"] = "说明", ["  Include my readme"] = "  包含我写的说明", ["README"] = "说明",
        // Cover (2026-11-22): the picture behind the skin's card in the player's library.
        ["Cover"] = "封面图", ["  Include my cover"] = "  包含我的封面图",
        ["Close"] = "关闭", ["Not credited"] = "未署名",
        ["Written by the skin's creator.  Mouse wheel to scroll."] = "这是皮肤作者自己写的说明。滚轮上下翻。",
    };

    private static readonly (string En, string Zh)[] Prefixes =
    {
        ("Skins folder: ", "皮肤文件夹："), ("Extracted this session: ", "本次已导出："), ("Dump On Load: ", "加载时导出："), ("Foreground: ", "前景："),
        ("Will be saved as  ", "将保存为  "), ("SkinCreator/Images:  ", "SkinCreator/Images：  "), ("Export page error: ", "导出页出错："),
        ("Preview picture (optional):  put a picture named preview.png in SkinForger/SkinCreator", "预览图（可选）：在 SkinForger/SkinCreator 里放一张名为 preview.png 的图"),
        ("SkinCreator/readme.txt will travel with the package",
         "说明：SkinCreator/readme.txt 会跟着皮肤包一起走。玩家会在你的皮肤上看到一个“说明”按钮，第一次打开这个皮肤时还会自动弹出来给他们看一次。"),
        ("SkinCreator/cover.png will travel with the package.",
         "封面图：SkinCreator/cover.png 会跟着皮肤包一起走。它会铺在玩家皮肤库里这个皮肤的卡片背景上，自动裁切填满整张卡，左边压黑，保证皮肤名字看得清。"),
        ("Optional:  put a picture named cover.png in SkinForger/SkinCreator",
         "可选：在 SkinForger/SkinCreator 里放一张名为 cover.png 的图，当作你皮肤在玩家皮肤库里的卡片背景。什么比例都行，会自动裁切填满；宽图能显示得最完整。"),
        ("Optional:  put a text file named readme.txt in SkinForger/SkinCreator",
         "可选：在 SkinForger/SkinCreator 里放一个名为 readme.txt 的文本文件，写上你想让玩家知道的事。用什么编辑器、存成什么编码都行；最多带走两万字。"),
        ("No skin folders in SkinCreator yet.", "SkinCreator 里还没有皮肤文件夹。先在“图片”页导出一个角色，在 SkinForger/SkinCreator/Images 里改图后再回来。"),
        ("No texts yet.", "还没有文字。点“导出文字”，把游戏里角色和小精灵的名字、介绍、技能、声优、台词和加载提示（中文和英文）导出到 SkinForger/SkinCreator/Text。"),
        ("Edited texts are used by the game", "改过的文字会在游戏下次显示时生效（重新打开角色界面，或等下一次加载画面）。"),
        ("by ", "作者："),
        ("Playing the original: ", "正在播放原版："), ("Playing your sound: ", "正在播放替换版："), ("Paused  ", "已暂停  "), ("Copied  ", "已复制  "),
        ("Festival Dialogue (", "节日台词（"), ("Voice:  ", "挂钩语音：  "), ("Muted: the bubble stays ", "已静音：气泡显示 "), ("└ (edited) ", "└（已改）"), ("Festival Review Dialogue (", "节日评价台词（"),
    };

    /// <summary>
    /// Patterns are compiled one at a time and a bad one is dropped (2026-11-19).
    ///
    /// These used to be built straight into a static field. A single invalid pattern — "Images\Other",
    /// where \O is not an escape a regex knows — threw inside the type initializer, which meant EVERY
    /// later touch of this class threw too. Loc wraps the whole window, so the creator window came up empty
    /// and the Skin Library would not open at all: one typo took the entire interface down.
    ///
    /// A translation is a convenience, never a reason for the window to stop working. So each pattern is
    /// built in its own try, a bad one is logged and skipped, and that string simply stays English.
    /// </summary>
    /// Built on first use, not as a field initializer: static fields run in declaration order, and
    /// RawPatterns is declared below this, so a field initializer here would have compiled an empty list.
    private static (Regex Pattern, string Zh)[]? _patterns;

    private static (Regex Pattern, string Zh)[] Patterns => _patterns ??= Compile(RawPatterns);

    private static (Regex, string)[] Compile((string En, string Zh)[] raw)
    {
        var built = new List<(Regex, string)>(raw.Length);
        foreach ((string pattern, string zh) in raw)
        {
            try { built.Add((new Regex(pattern), zh)); }
            catch (Exception exception)
            {
                MelonLogger.Warning($"{Prefix} pattern not used: {pattern} ({exception.GetBaseException().Message})");
            }
        }
        return built.ToArray();
    }

    private static readonly (string En, string Zh)[] RawPatterns =
    {
        (@"^(\d+) texts · (\d+) edited$", "共 $1 条 · 已改 $2 条"),
        (@"^Packing (\d+) / (\d+) files\.\.\.$", "正在打包 $1 / $2 个文件……"),
        (@"^(\d+) edited texts \(CN (\d+), EN (\d+)\)$", "改过的文字 $1 条（中文 $2，英文 $3）"),
        (@"^(\d+) pictures used by SkinCreator/CustomLodingPic/config\.json$", "SkinCreator/CustomLodingPic/config.json 使用的 $1 张图"),
        (@"^(\d+) pictures in SkinCreator/Images/Other, matched by texture name$", "SkinCreator/Images/Other 里的 $1 张图，按贴图名字配对"),
        (@"^(\d+) Other Pictures?$", "其他贴图 $1 张"),
        (@"^(.)  Pictures on screen \((\d+)\)$", "$1  屏幕上出现过的图片（$2）"),
        (@"^(\d+) Installed$", "已安装 $1 个"),
        (@"^(.+): (\d+) written to Images\\Other$", "$1：已写入 $2 张到 Images\\Other"),
        (@"^(.+): (\d+) written to Images\\Other, (\d+) already there$", "$1：已写入 $2 张到 Images\\Other，$3 张原本就有"),
        (@"^The game does not know a prefab or picture called ""(.+)""\.$", "游戏里没有叫“$1”的预制体或贴图。"),
        (@"^(.+) holds no picture that could be read\.$", "$1 里没有能读出来的贴图。"),
        // "Dialogue 2-3", "Loading Tip 7" ... — the label plus its place in the list.
        (@"^Dialogue (\d+)-(\d+)$", "台词 $1-$2"),
        (@"^Dialogue (\d+)$", "台词 $1"),
        (@"^Idle Dialogue (\d+)-?(\d*)$", "待机台词 $1$2"),
        (@"^Review Dialogue (\d+)-?(\d*)$", "评价台词 $1$2"),
        (@"^Cat Dialogue (\d+)-?(\d*)$", "猫化台词 $1$2"),
        (@"^April Fools Dialogue (\d+)-?(\d*)$", "愚人节台词 $1$2"),
        (@"^Loading Tip (\d+)$", "加载提示 $1"),
        (@"^(\d+) texts$", "$1 条文字"),
        (@"^(\d+) files$", "$1 个文件"),
        (@"^Cannot open the folder: (.+)$", "打不开文件夹：$1"),
        (@"^Install failed: (.+)$", "安装失败：$1"),
    };

    internal static string T(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (Renames.TryGetValue(text, out string? renamed)) text = renamed;   // every language
        if (!Active) return text;
        if (Exact.TryGetValue(text, out string? zh)) return zh;
        foreach ((string en, string translated) in Prefixes)
            if (text.StartsWith(en, StringComparison.Ordinal))
                return translated.EndsWith("。") || translated.EndsWith("图") ? translated : translated + text[en.Length..];
        foreach ((Regex pattern, string translated) in Patterns)
            if (pattern.IsMatch(text)) return pattern.Replace(text, translated);
        return text;
    }

    // ------------------------------------------------------------ hooks

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        int patched = 0;
        var prefix = new HarmonyMethod(typeof(Loc), nameof(CaptionPrefix));
        foreach (Type type in new[] { typeof(GUILayout), typeof(GUI) })
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name is not ("Label" or "Button" or "Toggle" or "Box" or "RepeatButton")) continue;
                if (!method.GetParameters().Any(p => p.Name == "text" && p.ParameterType == typeof(string))) continue;
                try { harmony.Patch(method, prefix: prefix); patched++; }
                catch (Exception exception) { MelonLogger.Warning($"{Prefix} {type.Name}.{method.Name} not patched: {exception.GetBaseException().Message}"); }
            }
        // Scopes: Main's creator window and Preview window (the Skin Library enters / exits itself).
        Type? host = AccessTools.TypeByName("SkinForger.SkinForgerMod");
        Type? preview = AccessTools.TypeByName("SkinForger.Preview.PreviewManager");
        foreach (MethodInfo? method in new[] { host == null ? null : AccessTools.DeclaredMethod(host, "DrawWindow"), preview == null ? null : AccessTools.DeclaredMethod(preview, "DrawPreviewWindow") })
        {
            if (method == null) continue;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Loc), nameof(ScopeEnter)) { priority = HarmonyLib.Priority.First },
                                  finalizer: new HarmonyMethod(typeof(Loc), nameof(ScopeExit)));
            patched++;
        }
        MelonLogger.Msg($"{Prefix} ready: {patched} hooks, {Exact.Count} texts");
        return patched;
    }

    private static void CaptionPrefix(ref string text)
    {
        if (_depth > 0 && text != null) text = T(text);
    }

    private static void ScopeEnter() => Enter();
    private static Exception? ScopeExit(Exception? __exception) { Exit(); return __exception; }
}
