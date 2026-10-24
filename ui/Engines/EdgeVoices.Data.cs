namespace ViaSonic.Engines;

/// <summary>Edge 朗读的全部音色数据（2026 年 10 月从微软的音色列表接口导出，共 322 个）。</summary>
public static partial class EdgeVoices
{
    /// <summary>中文音色有中文名；粤语（zh-HK）单独算一种语言。</summary>
    private static readonly VoiceItem[] Chinese =
    [
        new("zh-CN-YunxiNeural", "云希", "Yunxi"), // 音色名
        new("zh-CN-YunjianNeural", "云健", "Yunjian"), // 音色名
        new("zh-CN-YunyangNeural", "云扬", "Yunyang"), // 音色名
        new("zh-CN-YunxiaNeural", "云夏", "Yunxia"), // 音色名
        new("zh-CN-XiaoxiaoNeural", "晓晓", "Xiaoxiao"), // 音色名
        new("zh-CN-XiaoyiNeural", "晓伊", "Xiaoyi"), // 音色名
        new("zh-CN-liaoning-XiaobeiNeural", "晓北", "Xiaobei"), // 音色名
        new("zh-CN-shaanxi-XiaoniNeural", "晓妮", "Xiaoni"), // 音色名
        new("zh-TW-HsiaoChenNeural", "曉臻", "HsiaoChen"), // 音色名
        new("zh-TW-YunJheNeural", "雲哲", "YunJhe"), // 音色名
        new("zh-TW-HsiaoYuNeural", "曉雨", "HsiaoYu"), // 音色名
        new("zh-HK-WanLungNeural", "雲龍", "WanLung"), // 音色名
        new("zh-HK-HiuGaaiNeural", "曉佳", "HiuGaai"), // 音色名
        new("zh-HK-HiuMaanNeural", "曉曼", "HiuMaan"), // 音色名
    ];

    /// <summary>语言下拉框里的名称，前五个排在最前面，其余按名称排。</summary>
    private static readonly VoiceItem[] LangNames =
    [
        new("zh", "中文", "Chinese"), // 音色名
        new("yue", "粤语", "Cantonese"), // 音色名
        new("en", "英语", "English"), // 音色名
        new("ja", "日语", "Japanese"), // 音色名
        new("ko", "韩语", "Korean"), // 音色名
        new("af", "南非荷兰语", "Afrikaans"), // 音色名
        new("sq", "阿尔巴尼亚语", "Albanian"), // 音色名
        new("am", "阿姆哈拉语", "Amharic"), // 音色名
        new("ar", "阿拉伯语", "Arabic"), // 音色名
        new("az", "阿塞拜疆语", "Azerbaijani"), // 音色名
        new("bn", "孟加拉语", "Bengali"), // 音色名
        new("bs", "波斯尼亚语", "Bosnian"), // 音色名
        new("bg", "保加利亚语", "Bulgarian"), // 音色名
        new("my", "缅甸语", "Burmese"), // 音色名
        new("ca", "加泰罗尼亚语", "Catalan"), // 音色名
        new("hr", "克罗地亚语", "Croatian"), // 音色名
        new("cs", "捷克语", "Czech"), // 音色名
        new("da", "丹麦语", "Danish"), // 音色名
        new("nl", "荷兰语", "Dutch"), // 音色名
        new("et", "爱沙尼亚语", "Estonian"), // 音色名
        new("fil", "菲律宾语", "Filipino"), // 音色名
        new("fi", "芬兰语", "Finnish"), // 音色名
        new("fr", "法语", "French"), // 音色名
        new("gl", "加利西亚语", "Galician"), // 音色名
        new("ka", "格鲁吉亚语", "Georgian"), // 音色名
        new("de", "德语", "German"), // 音色名
        new("el", "希腊语", "Greek"), // 音色名
        new("gu", "古吉拉特语", "Gujarati"), // 音色名
        new("he", "希伯来语", "Hebrew"), // 音色名
        new("hi", "印地语", "Hindi"), // 音色名
        new("hu", "匈牙利语", "Hungarian"), // 音色名
        new("is", "冰岛语", "Icelandic"), // 音色名
        new("id", "印度尼西亚语", "Indonesian"), // 音色名
        new("iu", "因纽特语", "Inuktitut"), // 音色名
        new("ga", "爱尔兰语", "Irish"), // 音色名
        new("it", "意大利语", "Italian"), // 音色名
        new("jv", "爪哇语", "Javanese"), // 音色名
        new("kn", "卡纳达语", "Kannada"), // 音色名
        new("kk", "哈萨克语", "Kazakh"), // 音色名
        new("km", "高棉语", "Khmer"), // 音色名
        new("lo", "老挝语", "Lao"), // 音色名
        new("lv", "拉脱维亚语", "Latvian"), // 音色名
        new("lt", "立陶宛语", "Lithuanian"), // 音色名
        new("mk", "马其顿语", "Macedonian"), // 音色名
        new("ms", "马来语", "Malay"), // 音色名
        new("ml", "马拉雅拉姆语", "Malayalam"), // 音色名
        new("mt", "马耳他语", "Maltese"), // 音色名
        new("mr", "马拉地语", "Marathi"), // 音色名
        new("mn", "蒙古语", "Mongolian"), // 音色名
        new("ne", "尼泊尔语", "Nepali"), // 音色名
        new("nb", "挪威语", "Norwegian"), // 音色名
        new("ps", "普什图语", "Pashto"), // 音色名
        new("fa", "波斯语", "Persian"), // 音色名
        new("pl", "波兰语", "Polish"), // 音色名
        new("pt", "葡萄牙语", "Portuguese"), // 音色名
        new("ro", "罗马尼亚语", "Romanian"), // 音色名
        new("ru", "俄语", "Russian"), // 音色名
        new("sr", "塞尔维亚语", "Serbian"), // 音色名
        new("si", "僧伽罗语", "Sinhala"), // 音色名
        new("sk", "斯洛伐克语", "Slovak"), // 音色名
        new("sl", "斯洛文尼亚语", "Slovenian"), // 音色名
        new("so", "索马里语", "Somali"), // 音色名
        new("es", "西班牙语", "Spanish"), // 音色名
        new("su", "巽他语", "Sundanese"), // 音色名
        new("sw", "斯瓦希里语", "Swahili"), // 音色名
        new("sv", "瑞典语", "Swedish"), // 音色名
        new("ta", "泰米尔语", "Tamil"), // 音色名
        new("te", "泰卢固语", "Telugu"), // 音色名
        new("th", "泰语", "Thai"), // 音色名
        new("tr", "土耳其语", "Turkish"), // 音色名
        new("uk", "乌克兰语", "Ukrainian"), // 音色名
        new("ur", "乌尔都语", "Urdu"), // 音色名
        new("uz", "乌兹别克语", "Uzbek"), // 音色名
        new("vi", "越南语", "Vietnamese"), // 音色名
        new("cy", "威尔士语", "Welsh"), // 音色名
        new("zu", "祖鲁语", "Zulu"), // 音色名
    ];

    /// <summary>同一种语言里有重名的音色时，名字后面标上区别（只有因纽特语：两种文字各有一个同名音色）。</summary>
    private static readonly VoiceItem[] RegionNames =
    [
        new("Latn", "拉丁字母", "Latin"), // 音色名
        new("Cans", "音节文字", "Syllabics"), // 音色名
    ];

    /// <summary>其他语言：(locale, 名字)，音色 ID 是「locale-名字Neural」。同一语言里主要地区排前面。</summary>
    private static readonly (string Locale, string Name)[] Others =
    [
        ("af-ZA", "Adri"),
        ("af-ZA", "Willem"),
        ("am-ET", "Ameha"),
        ("am-ET", "Mekdes"),
        ("ar-SA", "Hamed"),
        ("ar-SA", "Zariyah"),
        ("ar-DZ", "Amina"),
        ("ar-DZ", "Ismael"),
        ("ar-BH", "Ali"),
        ("ar-BH", "Laila"),
        ("ar-EG", "Salma"),
        ("ar-EG", "Shakir"),
        ("ar-IQ", "Bassel"),
        ("ar-IQ", "Rana"),
        ("ar-JO", "Sana"),
        ("ar-JO", "Taim"),
        ("ar-KW", "Fahed"),
        ("ar-KW", "Noura"),
        ("ar-LB", "Layla"),
        ("ar-LB", "Rami"),
        ("ar-LY", "Iman"),
        ("ar-LY", "Omar"),
        ("ar-MA", "Jamal"),
        ("ar-MA", "Mouna"),
        ("ar-OM", "Abdullah"),
        ("ar-OM", "Aysha"),
        ("ar-QA", "Amal"),
        ("ar-QA", "Moaz"),
        ("ar-SY", "Amany"),
        ("ar-SY", "Laith"),
        ("ar-TN", "Hedi"),
        ("ar-TN", "Reem"),
        ("ar-AE", "Fatima"),
        ("ar-AE", "Hamdan"),
        ("ar-YE", "Maryam"),
        ("ar-YE", "Saleh"),
        ("az-AZ", "Babek"),
        ("az-AZ", "Banu"),
        ("bg-BG", "Borislav"),
        ("bg-BG", "Kalina"),
        ("bn-BD", "Nabanita"),
        ("bn-BD", "Pradeep"),
        ("bn-IN", "Bashkar"),
        ("bn-IN", "Tanishaa"),
        ("bs-BA", "Vesna"),
        ("bs-BA", "Goran"),
        ("ca-ES", "Enric"),
        ("ca-ES", "Joana"),
        ("cs-CZ", "Antonin"),
        ("cs-CZ", "Vlasta"),
        ("cy-GB", "Aled"),
        ("cy-GB", "Nia"),
        ("da-DK", "Christel"),
        ("da-DK", "Jeppe"),
        ("de-DE", "SeraphinaMultilingual"),
        ("de-DE", "FlorianMultilingual"),
        ("de-DE", "Amala"),
        ("de-DE", "Conrad"),
        ("de-DE", "Katja"),
        ("de-DE", "Killian"),
        ("de-AT", "Ingrid"),
        ("de-AT", "Jonas"),
        ("de-CH", "Jan"),
        ("de-CH", "Leni"),
        ("el-GR", "Athina"),
        ("el-GR", "Nestoras"),
        ("en-US", "Ava"),
        ("en-US", "Andrew"),
        ("en-US", "Emma"),
        ("en-US", "Brian"),
        ("en-US", "Ana"),
        ("en-US", "AndrewMultilingual"),
        ("en-US", "Aria"),
        ("en-US", "AvaMultilingual"),
        ("en-US", "BrianMultilingual"),
        ("en-US", "Christopher"),
        ("en-US", "EmmaMultilingual"),
        ("en-US", "Eric"),
        ("en-US", "Guy"),
        ("en-US", "Jenny"),
        ("en-US", "Michelle"),
        ("en-US", "Roger"),
        ("en-US", "Steffan"),
        ("en-AU", "WilliamMultilingual"),
        ("en-AU", "Natasha"),
        ("en-CA", "Clara"),
        ("en-CA", "Liam"),
        ("en-HK", "Yan"),
        ("en-HK", "Sam"),
        ("en-IN", "NeerjaExpressive"),
        ("en-IN", "Neerja"),
        ("en-IN", "Prabhat"),
        ("en-IE", "Connor"),
        ("en-IE", "Emily"),
        ("en-KE", "Asilia"),
        ("en-KE", "Chilemba"),
        ("en-NZ", "Mitchell"),
        ("en-NZ", "Molly"),
        ("en-NG", "Abeo"),
        ("en-NG", "Ezinne"),
        ("en-PH", "James"),
        ("en-PH", "Rosa"),
        ("en-SG", "Luna"),
        ("en-SG", "Wayne"),
        ("en-ZA", "Leah"),
        ("en-ZA", "Luke"),
        ("en-TZ", "Elimu"),
        ("en-TZ", "Imani"),
        ("en-GB", "Libby"),
        ("en-GB", "Maisie"),
        ("en-GB", "Ryan"),
        ("en-GB", "Sonia"),
        ("en-GB", "Thomas"),
        ("es-ES", "Ximena"),
        ("es-ES", "Alvaro"),
        ("es-ES", "Elvira"),
        ("es-AR", "Elena"),
        ("es-AR", "Tomas"),
        ("es-BO", "Marcelo"),
        ("es-BO", "Sofia"),
        ("es-CL", "Catalina"),
        ("es-CL", "Lorenzo"),
        ("es-CO", "Gonzalo"),
        ("es-CO", "Salome"),
        ("es-CR", "Juan"),
        ("es-CR", "Maria"),
        ("es-CU", "Belkys"),
        ("es-CU", "Manuel"),
        ("es-DO", "Emilio"),
        ("es-DO", "Ramona"),
        ("es-EC", "Andrea"),
        ("es-EC", "Luis"),
        ("es-SV", "Lorena"),
        ("es-SV", "Rodrigo"),
        ("es-GQ", "Javier"),
        ("es-GQ", "Teresa"),
        ("es-GT", "Andres"),
        ("es-GT", "Marta"),
        ("es-HN", "Carlos"),
        ("es-HN", "Karla"),
        ("es-MX", "Dalia"),
        ("es-MX", "Jorge"),
        ("es-NI", "Federico"),
        ("es-NI", "Yolanda"),
        ("es-PA", "Margarita"),
        ("es-PA", "Roberto"),
        ("es-PY", "Mario"),
        ("es-PY", "Tania"),
        ("es-PE", "Alex"),
        ("es-PE", "Camila"),
        ("es-PR", "Karina"),
        ("es-PR", "Victor"),
        ("es-US", "Alonso"),
        ("es-US", "Paloma"),
        ("es-UY", "Mateo"),
        ("es-UY", "Valentina"),
        ("es-VE", "Paola"),
        ("es-VE", "Sebastian"),
        ("et-EE", "Anu"),
        ("et-EE", "Kert"),
        ("fa-IR", "Dilara"),
        ("fa-IR", "Farid"),
        ("fi-FI", "Harri"),
        ("fi-FI", "Noora"),
        ("fil-PH", "Angelo"),
        ("fil-PH", "Blessica"),
        ("fr-FR", "VivienneMultilingual"),
        ("fr-FR", "RemyMultilingual"),
        ("fr-FR", "Denise"),
        ("fr-FR", "Eloise"),
        ("fr-FR", "Henri"),
        ("fr-BE", "Charline"),
        ("fr-BE", "Gerard"),
        ("fr-CA", "Thierry"),
        ("fr-CA", "Antoine"),
        ("fr-CA", "Jean"),
        ("fr-CA", "Sylvie"),
        ("fr-CH", "Ariane"),
        ("fr-CH", "Fabrice"),
        ("ga-IE", "Colm"),
        ("ga-IE", "Orla"),
        ("gl-ES", "Roi"),
        ("gl-ES", "Sabela"),
        ("gu-IN", "Dhwani"),
        ("gu-IN", "Niranjan"),
        ("he-IL", "Avri"),
        ("he-IL", "Hila"),
        ("hi-IN", "Madhur"),
        ("hi-IN", "Swara"),
        ("hr-HR", "Gabrijela"),
        ("hr-HR", "Srecko"),
        ("hu-HU", "Noemi"),
        ("hu-HU", "Tamas"),
        ("id-ID", "Ardi"),
        ("id-ID", "Gadis"),
        ("is-IS", "Gudrun"),
        ("is-IS", "Gunnar"),
        ("it-IT", "GiuseppeMultilingual"),
        ("it-IT", "Diego"),
        ("it-IT", "Elsa"),
        ("it-IT", "Isabella"),
        ("iu-Latn-CA", "Siqiniq"),
        ("iu-Latn-CA", "Taqqiq"),
        ("iu-Cans-CA", "Siqiniq"),
        ("iu-Cans-CA", "Taqqiq"),
        ("ja-JP", "Keita"),
        ("ja-JP", "Nanami"),
        ("jv-ID", "Dimas"),
        ("jv-ID", "Siti"),
        ("ka-GE", "Eka"),
        ("ka-GE", "Giorgi"),
        ("kk-KZ", "Aigul"),
        ("kk-KZ", "Daulet"),
        ("km-KH", "Piseth"),
        ("km-KH", "Sreymom"),
        ("kn-IN", "Gagan"),
        ("kn-IN", "Sapna"),
        ("ko-KR", "HyunsuMultilingual"),
        ("ko-KR", "InJoon"),
        ("ko-KR", "SunHi"),
        ("lo-LA", "Chanthavong"),
        ("lo-LA", "Keomany"),
        ("lt-LT", "Leonas"),
        ("lt-LT", "Ona"),
        ("lv-LV", "Everita"),
        ("lv-LV", "Nils"),
        ("mk-MK", "Aleksandar"),
        ("mk-MK", "Marija"),
        ("ml-IN", "Midhun"),
        ("ml-IN", "Sobhana"),
        ("mn-MN", "Bataa"),
        ("mn-MN", "Yesui"),
        ("mr-IN", "Aarohi"),
        ("mr-IN", "Manohar"),
        ("ms-MY", "Osman"),
        ("ms-MY", "Yasmin"),
        ("mt-MT", "Grace"),
        ("mt-MT", "Joseph"),
        ("my-MM", "Nilar"),
        ("my-MM", "Thiha"),
        ("nb-NO", "Finn"),
        ("nb-NO", "Pernille"),
        ("ne-NP", "Hemkala"),
        ("ne-NP", "Sagar"),
        ("nl-NL", "Colette"),
        ("nl-NL", "Fenna"),
        ("nl-NL", "Maarten"),
        ("nl-BE", "Arnaud"),
        ("nl-BE", "Dena"),
        ("pl-PL", "Marek"),
        ("pl-PL", "Zofia"),
        ("ps-AF", "GulNawaz"),
        ("ps-AF", "Latifa"),
        ("pt-BR", "ThalitaMultilingual"),
        ("pt-BR", "Antonio"),
        ("pt-BR", "Francisca"),
        ("pt-PT", "Duarte"),
        ("pt-PT", "Raquel"),
        ("ro-RO", "Alina"),
        ("ro-RO", "Emil"),
        ("ru-RU", "Dmitry"),
        ("ru-RU", "Svetlana"),
        ("si-LK", "Sameera"),
        ("si-LK", "Thilini"),
        ("sk-SK", "Lukas"),
        ("sk-SK", "Viktoria"),
        ("sl-SI", "Petra"),
        ("sl-SI", "Rok"),
        ("so-SO", "Muuse"),
        ("so-SO", "Ubax"),
        ("sq-AL", "Anila"),
        ("sq-AL", "Ilir"),
        ("sr-RS", "Nicholas"),
        ("sr-RS", "Sophie"),
        ("su-ID", "Jajang"),
        ("su-ID", "Tuti"),
        ("sv-SE", "Mattias"),
        ("sv-SE", "Sofie"),
        ("sw-KE", "Rafiki"),
        ("sw-KE", "Zuri"),
        ("sw-TZ", "Daudi"),
        ("sw-TZ", "Rehema"),
        ("ta-IN", "Pallavi"),
        ("ta-IN", "Valluvar"),
        ("ta-MY", "Kani"),
        ("ta-MY", "Surya"),
        ("ta-SG", "Anbu"),
        ("ta-SG", "Venba"),
        ("ta-LK", "Kumar"),
        ("ta-LK", "Saranya"),
        ("te-IN", "Mohan"),
        ("te-IN", "Shruti"),
        ("th-TH", "Niwat"),
        ("th-TH", "Premwadee"),
        ("tr-TR", "Emel"),
        ("tr-TR", "Ahmet"),
        ("uk-UA", "Ostap"),
        ("uk-UA", "Polina"),
        ("ur-PK", "Asad"),
        ("ur-PK", "Uzma"),
        ("ur-IN", "Gul"),
        ("ur-IN", "Salman"),
        ("uz-UZ", "Madina"),
        ("uz-UZ", "Sardor"),
        ("vi-VN", "HoaiMy"),
        ("vi-VN", "NamMinh"),
        ("zu-ZA", "Thando"),
        ("zu-ZA", "Themba"),
    ];
}
