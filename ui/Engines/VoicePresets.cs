namespace ViaSonic.Engines;

/// <summary>
/// 没有音色列表接口的服务用的预置音色，名称取自各家官方音色表。
/// 行尾的「// 音色名」让 tools/check_lang.py 跳过这些数据（英文名写在第三列，不进翻译表）。
/// </summary>
public static class VoicePresets
{
    /// <summary>讯飞在线语音合成的基础发音人（参数值 vcn）。其他发音人在讯飞控制台添加后，用 ＋ 填进来。</summary>
    public static readonly VoiceItem[] Xunfei =
    [
        new("xiaoyan", "小燕（女）", "Xiaoyan (female)"), // 音色名
        new("aisjiuxu", "许久（男）", "Xu Jiu (male)"), // 音色名
        new("aisxping", "小萍（女）", "Xiaoping (female)"), // 音色名
        new("aisjinger", "小婧（女）", "Xiaojing (female)"), // 音色名
        new("aisbabyxu", "许小宝（童声）", "Xu Xiaobao (child)"), // 音色名
    ];

    /// <summary>豆包语音合成模型 2.0（seed-tts-2.0）。</summary>
    public static readonly VoiceItem[] Doubao2 =
    [
        new("zh_female_vv_uranus_bigtts", "Vivi 2.0", "Vivi 2.0"),
        new("zh_female_xiaohe_uranus_bigtts", "小何 2.0", "Xiaohe 2.0"), // 音色名
        new("zh_male_m191_uranus_bigtts", "云舟 2.0", "Yunzhou 2.0"), // 音色名
        new("zh_male_taocheng_uranus_bigtts", "小天 2.0", "Xiaotian 2.0"), // 音色名
        new("zh_male_liufei_uranus_bigtts", "刘飞 2.0", "Liu Fei 2.0"), // 音色名
        new("zh_female_sophie_uranus_bigtts", "魅力苏菲 2.0", "Sophie 2.0"), // 音色名
        new("zh_female_qingxinnvsheng_uranus_bigtts", "清新女声 2.0", "Fresh Female 2.0"), // 音色名
        new("zh_female_cancan_uranus_bigtts", "知性灿灿 2.0", "Cancan 2.0"), // 音色名
        new("zh_female_sajiaoxuemei_uranus_bigtts", "撒娇学妹 2.0", "Sweet Junior 2.0"), // 音色名
        new("zh_female_tianmeixiaoyuan_uranus_bigtts", "甜美小源 2.0", "Xiaoyuan 2.0"), // 音色名
        new("zh_female_tianmeitaozi_uranus_bigtts", "甜美桃子 2.0", "Taozi 2.0"), // 音色名
        new("zh_female_shuangkuaisisi_uranus_bigtts", "爽快思思 2.0", "Sisi 2.0"), // 音色名
        new("zh_female_linjianvhai_uranus_bigtts", "邻家女孩 2.0", "Girl Next Door 2.0"), // 音色名
        new("zh_male_shaonianzixin_uranus_bigtts", "少年梓辛 2.0", "Zixin 2.0"), // 音色名
        new("zh_male_wennuanahu_uranus_bigtts", "温暖阿虎 2.0", "Ahu 2.0"), // 音色名
        new("zh_male_jieshuoxiaoming_uranus_bigtts", "解说小明 2.0", "Narrator Xiaoming 2.0"), // 音色名
        new("zh_male_shenyeboke_uranus_bigtts", "深夜播客 2.0", "Late-night Podcast 2.0"), // 音色名
        new("zh_male_xuanyijieshuo_uranus_bigtts", "悬疑解说 2.0", "Mystery Narrator 2.0"), // 音色名
        new("zh_female_gaolengyujie_uranus_bigtts", "高冷御姐 2.0", "Aloof Lady 2.0"), // 音色名
        new("zh_male_aojiaobazong_uranus_bigtts", "傲娇霸总 2.0", "Proud CEO 2.0"), // 音色名
        new("zh_male_naiqimengwa_uranus_bigtts", "奶气萌娃 2.0", "Cute Kid 2.0"), // 音色名
        new("zh_male_sunwukong_uranus_bigtts", "猴哥 2.0", "Monkey King 2.0"), // 音色名
        new("zh_male_zhubajie_uranus_bigtts", "猪八戒 2.0", "Zhu Bajie 2.0"), // 音色名
        new("zh_male_tangseng_uranus_bigtts", "唐僧 2.0", "Tang Monk 2.0"), // 音色名
        new("zh_male_lubanqihao_uranus_bigtts", "鲁班七号 2.0", "Luban No.7 2.0"), // 音色名
        new("zh_male_xionger_uranus_bigtts", "熊二 2.0", "Xiong Er 2.0"), // 音色名
        new("zh_female_peiqi_uranus_bigtts", "佩奇猪 2.0", "Peppa 2.0"), // 音色名
        new("zh_female_yingtaowanzi_uranus_bigtts", "樱桃丸子 2.0", "Maruko 2.0"), // 音色名
        new("en_male_tim_uranus_bigtts", "Tim", "Tim"),
        new("en_female_dacey_uranus_bigtts", "Dacey", "Dacey"),
        new("en_female_stokie_uranus_bigtts", "Stokie", "Stokie"),
    ];

    /// <summary>豆包语音合成模型 1.0（seed-tts-1.0）。</summary>
    public static readonly VoiceItem[] Doubao1 =
    [
        new("zh_female_shuangkuaisisi_moon_bigtts", "爽快思思", "Skye"), // 音色名
        new("zh_male_wennuanahu_moon_bigtts", "温暖阿虎", "Alvin"), // 音色名
        new("zh_male_shaonianzixin_moon_bigtts", "少年梓辛", "Brayan"), // 音色名
        new("zh_female_cancan_mars_bigtts", "灿灿", "Shiny"), // 音色名
        new("zh_female_linjianvhai_moon_bigtts", "邻家女孩", "Girl Next Door"), // 音色名
        new("zh_male_yangguangqingnian_moon_bigtts", "阳光青年", "Sunny Youth"), // 音色名
        new("zh_female_tianmeixiaoyuan_moon_bigtts", "甜美小源", "Xiaoyuan"), // 音色名
        new("zh_male_jieshuoxiaoming_moon_bigtts", "解说小明", "Narrator Xiaoming"), // 音色名
        new("zh_female_gaolengyujie_moon_bigtts", "高冷御姐", "Aloof Lady"), // 音色名
        new("zh_male_aojiaobazong_moon_bigtts", "傲娇霸总", "Proud CEO"), // 音色名
        new("zh_female_meilinvyou_moon_bigtts", "魅力女友", "Charming Girlfriend"), // 音色名
        new("zh_male_shenyeboke_moon_bigtts", "深夜播客", "Late-night Podcast"), // 音色名
        new("zh_male_sunwukong_mars_bigtts", "猴哥", "Monkey King"), // 音色名
        new("zh_male_xionger_mars_bigtts", "熊二", "Xiong Er"), // 音色名
        new("zh_female_peiqi_mars_bigtts", "佩奇猪", "Peppa"), // 音色名
        new("zh_female_yingtaowanzi_mars_bigtts", "樱桃丸子", "Maruko"), // 音色名
        new("zh_male_naiqimengwa_mars_bigtts", "奶气萌娃", "Cute Kid"), // 音色名
        new("zh_male_beijingxiaoye_moon_bigtts", "北京小爷", "Beijing Guy"), // 音色名
        new("zh_female_daimengchuanmei_moon_bigtts", "呆萌川妹", "Sichuan Girl"), // 音色名
        new("zh_female_wanwanxiaohe_moon_bigtts", "湾湾小何", "Taiwanese Xiaohe"), // 音色名
        new("zh_male_guozhoudege_moon_bigtts", "广州德哥", "Guangzhou Dege"), // 音色名
        new("zh_female_yueyunv_mars_bigtts", "粤语小溏", "Cantonese Xiaotang"), // 音色名
        new("en_male_adam_mars_bigtts", "Adam", "Adam"),
        new("en_female_amanda_mars_bigtts", "Amanda", "Amanda"),
    ];

    /// <summary>阿里云百炼 Qwen3-TTS（qwen3-tts-flash），音色参数本身就是英文名。</summary>
    public static readonly VoiceItem[] Qwen =
    [
        new("Cherry", "芊悦", "Cherry"), // 音色名
        new("Serena", "苏瑶", "Serena"), // 音色名
        new("Ethan", "晨煦", "Ethan"), // 音色名
        new("Chelsie", "千雪", "Chelsie"), // 音色名
        new("Momo", "茉兔", "Momo"), // 音色名
        new("Vivian", "十三", "Vivian"), // 音色名
        new("Moon", "月白", "Moon"), // 音色名
        new("Maia", "四月", "Maia"), // 音色名
        new("Kai", "凯", "Kai"), // 音色名
        new("Nofish", "不吃鱼", "Nofish"), // 音色名
        new("Bella", "萌宝", "Bella"), // 音色名
        new("Jennifer", "詹妮弗", "Jennifer"), // 音色名
        new("Ryan", "甜茶", "Ryan"), // 音色名
        new("Katerina", "卡捷琳娜", "Katerina"), // 音色名
        new("Aiden", "艾登", "Aiden"), // 音色名
        new("Eldric Sage", "沧明子", "Eldric Sage"), // 音色名
        new("Mia", "乖小妹", "Mia"), // 音色名
        new("Mochi", "沙小弥", "Mochi"), // 音色名
        new("Bellona", "燕铮莺", "Bellona"), // 音色名
        new("Vincent", "田叔", "Vincent"), // 音色名
        new("Bunny", "萌小姬", "Bunny"), // 音色名
        new("Neil", "阿闻", "Neil"), // 音色名
        new("Elias", "墨讲师", "Elias"), // 音色名
        new("Arthur", "徐大爷", "Arthur"), // 音色名
        new("Nini", "邻家妹妹", "Nini"), // 音色名
        new("Seren", "小婉", "Seren"), // 音色名
        new("Pip", "顽屁小孩", "Pip"), // 音色名
        new("Stella", "少女阿月", "Stella"), // 音色名
        new("Bodega", "博德加", "Bodega"), // 音色名
        new("Sonrisa", "索尼莎", "Sonrisa"), // 音色名
        new("Alek", "阿列克", "Alek"), // 音色名
        new("Dolce", "多尔切", "Dolce"), // 音色名
        new("Sohee", "素熙", "Sohee"), // 音色名
        new("Ono Anna", "小野杏", "Ono Anna"), // 音色名
        new("Lenn", "莱恩", "Lenn"), // 音色名
        new("Emilien", "埃米尔安", "Emilien"), // 音色名
        new("Andre", "安德雷", "Andre"), // 音色名
        new("Radio Gol", "拉迪奥·戈尔", "Radio Gol"), // 音色名
        new("Jada", "上海-阿珍", "Jada (Shanghainese)"), // 音色名
        new("Dylan", "北京-晓东", "Dylan (Beijing)"), // 音色名
        new("Li", "南京-老李", "Li (Nanjing)"), // 音色名
        new("Marcus", "陕西-秦川", "Marcus (Shaanxi)"), // 音色名
        new("Roy", "闽南-阿杰", "Roy (Min Nan)"), // 音色名
        new("Peter", "天津-李彼得", "Peter (Tianjin)"), // 音色名
        new("Sunny", "四川-晴儿", "Sunny (Sichuanese)"), // 音色名
        new("Eric", "四川-程川", "Eric (Sichuanese)"), // 音色名
        new("Rocky", "粤语-阿强", "Rocky (Cantonese)"), // 音色名
        new("Kiki", "粤语-阿清", "Kiki (Cantonese)"), // 音色名
    ];

    /// <summary>阿里云百炼 CosyVoice（cosyvoice-v3-flash）。</summary>
    public static readonly VoiceItem[] Cosy =
    [
        new("longanyang", "龙安洋", "Long Anyang"), // 音色名
        new("longanhuan", "龙安欢", "Long Anhuan"), // 音色名
        new("longxiaochun_v3", "龙小淳", "Long Xiaochun"), // 音色名
        new("longxiaoxia_v3", "龙小夏", "Long Xiaoxia"), // 音色名
        new("longyingxiao_v3", "龙应笑", "Long Yingxiao"), // 音色名
        new("longyingxun_v3", "龙应询", "Long Yingxun"), // 音色名
        new("longyingjing_v3", "龙应静", "Long Yingjing"), // 音色名
        new("longyingling_v3", "龙应聆", "Long Yingling"), // 音色名
        new("longyingtao_v3", "龙应桃", "Long Yingtao"), // 音色名
        new("longanwen_v3", "龙安温", "Long Anwen"), // 音色名
        new("longanli_v3", "龙安莉", "Long Anli"), // 音色名
        new("longanlang_v3", "龙安朗", "Long Anlang"), // 音色名
        new("longanyun_v3", "龙安昀", "Long Anyun"), // 音色名
        new("longhua_v3", "龙华", "Long Hua"), // 音色名
        new("longze_v3", "龙泽", "Long Ze"), // 音色名
        new("longshu_v3", "龙书", "Long Shu"), // 音色名
        new("longshuo_v3", "龙硕", "Long Shuo"), // 音色名
        new("loongbella_v3", "Bella3.0", "Bella 3.0"),
        new("loongabby_v3", "Abby", "Abby"),
        new("loongandy_v3", "Andy", "Andy"),
        new("loongemily_v3", "Emily", "Emily"),
    ];

    public static readonly string[] AzureRegions =
    [
        "eastasia", "southeastasia", "japaneast", "koreacentral", "centralindia", "australiaeast",
        "eastus", "eastus2", "westus", "westus2", "westus3", "centralus", "canadacentral", "brazilsouth",
        "westeurope", "northeurope", "uksouth", "francecentral", "germanywestcentral", "swedencentral", "switzerlandnorth",
    ];

    public static readonly string[] MiniMaxModels =
        ["speech-2.8-hd", "speech-2.8-turbo", "speech-2.6-hd", "speech-2.6-turbo", "speech-02-hd", "speech-02-turbo"];
}
