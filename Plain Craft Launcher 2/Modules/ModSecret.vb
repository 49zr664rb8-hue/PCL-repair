'本文件在上游开源版中被删减了部分内容（加解密、CDN 签名、更新与统计等）。
'PCL3 修改版的处理原则：
'  1. 不再硬编码任何密钥，全部改为从外部配置 / 环境变量读取；
'  2. 原本被固定写死的功能开关（如 RestrictMcDownload）改为可配置项；
'  3. 被上游删除的加解密逻辑不再伪造，改为直连或使用公开接口。

Friend Module ModSecret

    '标注 PCL 的不同分支，仅用于替换标记
    Public Const VersionBranchMain As String = "Community"
    '在开源版的注册表与常规版的注册表隔离，以防数据冲突
    Public Const RegFolder As String = "PCLDebug"
    '用于匿名数据收集的腾讯云日志服务上报 URL（开源版无此功能，保留为空）
    Public Const ClsBaseUrl As String = ""

#Region "本地配置"

    ''' <summary>
    ''' 外部配置文件名，需与 PCL 本体放在同一目录。
    ''' </summary>
    Public Const LocalConfigFileName As String = "PCL3.config.ini"

    Private LocalConfigCache As Dictionary(Of String, String) = Nothing

    ''' <summary>
    ''' 读取外部配置。惰性加载，整个进程只读一次文件。
    ''' 格式为 key=value，支持 # ; ' 注释与 [Section] 分节；
    ''' 分节内的键既能用「键名」也能用「节名.键名」访问。
    ''' 优先级：环境变量 PCL3_xxx &gt; 配置文件 &gt; 默认值。
    ''' </summary>
    Friend Function LocalConfigGet(Key As String, Optional DefaultValue As String = "") As String
        '环境变量优先，便于临时覆盖而不必改文件
        Dim EnvValue As String = Environment.GetEnvironmentVariable("PCL3_" & Key.Replace(".", "_").ToUpper())
        If Not String.IsNullOrWhiteSpace(EnvValue) Then Return EnvValue.Trim()
        If LocalConfigCache Is Nothing Then LocalConfigLoad()
        Dim Value As String = Nothing
        If LocalConfigCache.TryGetValue(Key, Value) AndAlso Not String.IsNullOrWhiteSpace(Value) Then Return Value
        Return If(DefaultValue, "")
    End Function

    Friend Function LocalConfigGetBool(Key As String, DefaultValue As Boolean) As Boolean
        Dim Raw As String = LocalConfigGet(Key, If(DefaultValue, "true", "false")).Trim().ToLower()
        Select Case Raw
            Case "1", "true", "yes", "on", "是", "开", "启用"
                Return True
            Case "0", "false", "no", "off", "否", "关", "禁用"
                Return False
            Case Else
                Return DefaultValue
        End Select
    End Function

    Private Sub LocalConfigLoad()
        LocalConfigCache = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Try
            Dim ConfigPath As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LocalConfigFileName)
            If Not FileUtils.Exists(ConfigPath) Then Return
            Dim Lines As String() = FileUtils.TryReadAsLines(ConfigPath)
            If Lines Is Nothing Then Return
            Dim Section As String = ""
            For Each RawLine As String In Lines
                Dim Line As String = RawLine.Trim()
                If Line.Length = 0 Then Continue For
                If Line.StartsWith("#") OrElse Line.StartsWith(";") OrElse Line.StartsWith("'") Then Continue For
                If Line.StartsWith("[") AndAlso Line.EndsWith("]") Then
                    Section = Line.Substring(1, Line.Length - 2).Trim()
                    Continue For
                End If
                Dim SplitIndex As Integer = Line.IndexOf("="c)
                If SplitIndex <= 0 Then Continue For
                Dim Key As String = Line.Substring(0, SplitIndex).Trim()
                Dim Value As String = Line.Substring(SplitIndex + 1).Trim()
                LocalConfigCache(Key) = Value
                If Section.Length > 0 Then LocalConfigCache(Section & "." & Key) = Value
            Next
        Catch ex As Exception
            Logger.Error(ex, "读取本地配置文件失败", LogBehavior.Toast)
        End Try
    End Sub

#End Region

#Region "网络鉴权"

    ''' <summary>
    ''' 是否限制原版 Minecraft 下载。
    ''' 上游开源版此处固定为 True，导致下载页被整体隐藏；本修改版改为读取配置，默认解锁。
    ''' 下载来源仍然只有 Mojang 官方与 BMCLAPI 等公开镜像，本程序不托管任何游戏文件。
    ''' </summary>
    Public ReadOnly Property RestrictMcDownload As Boolean
        Get
            Return Not LocalConfigGetBool("Download.UnlockVanilla", True)
        End Get
    End Property

    ''' <summary>
    ''' 用于微软正版登录的 ClientId。需自行在 Azure 门户注册应用后填入，留空则微软登录不可用。
    ''' </summary>
    Public ReadOnly Property OAuthClientId As String
        Get
            Return LocalConfigGet("Auth.MsClientId", Environment.GetEnvironmentVariable("PCL_MS_CLIENT_ID"))
        End Get
    End Property

    ''' <summary>
    ''' CurseForge API Key。留空则 CurseForge 源不可用（会自动回退到 Modrinth），Modrinth 本身无需密钥。
    ''' </summary>
    Public ReadOnly Property CurseForgeAPIKey As String
        Get
            Return LocalConfigGet("Auth.CurseForgeApiKey", Environment.GetEnvironmentVariable("PCL_CURSEFORGE_API_KEY"))
        End Get
    End Property

    Friend Function SecretCdnSign(UrlWithMark As String) As String
        If Not UrlWithMark.EndsWithF("{CDN}") Then Return UrlWithMark
        Return UrlWithMark.Replace("{CDN}", "").Replace(" ", "%20")
    End Function
    ''' <summary>
    ''' 设置 Headers 的 UA、Referer。
    ''' </summary>
    Friend Sub SecretHeadersSign(Url As String, ByRef Req As HttpRequestMessage, Optional SimulateBrowserHeaders As Boolean = False)
        If Not Req.Headers.UserAgent.Any Then
            If Url.Contains("baidupcs.com") OrElse Url.Contains("baidu.com") Then
                Req.Headers.Add("User-Agent", "LogStatistic")  '#4951
            ElseIf SimulateBrowserHeaders Then
                Req.Headers.Add("User-Agent", $"PCL2/{VersionBaseName}.{CInt(BuildType)} Mozilla/5.0 AppleWebKit/537.36 Chrome/63.0.3239.132 Safari/537.36")
            Else
                Req.Headers.Add("User-Agent", $"PCL2/{VersionBaseName}.{CInt(BuildType)}")
            End If
        End If
        If Not SimulateBrowserHeaders Then Req.Headers.Add("Referer", $"http://{VersionCode}.open.pcl2.server/")
        '未配置 CurseForge Key 时不发送空密钥，避免被服务端直接 403，从而能正常回退到 Modrinth
        If (Url.Contains("api.curseforge.com") OrElse Url.Contains("forgecdn.net")) AndAlso Not String.IsNullOrWhiteSpace(CurseForgeAPIKey) Then
            Req.Headers.Add("x-api-key", CurseForgeAPIKey)
        End If
    End Sub

#End Region

#Region "主题"

    Public Color1 As New MyColor(52, 61, 74)
    Public Color2 As New MyColor(11, 91, 203)
    Public Color3 As New MyColor(19, 112, 243)
    Public Color4 As New MyColor(72, 144, 245)
    Public Color5 As New MyColor(150, 192, 249)
    Public Color6 As New MyColor(213, 230, 253)
    Public Color7 As New MyColor(222, 236, 253)
    Public Color8 As New MyColor(234, 242, 254)
    Public ColorBg0 As New MyColor(150, 192, 249)
    Public ColorBg1 As New MyColor(190, Color7)
    Public ColorGray1 As New MyColor(64, 64, 64)
    Public ColorGray2 As New MyColor(115, 115, 115)
    Public ColorGray3 As New MyColor(140, 140, 140)
    Public ColorGray4 As New MyColor(166, 166, 166)
    Public ColorGray5 As New MyColor(204, 204, 204)
    Public ColorGray6 As New MyColor(235, 235, 235)
    Public ColorGray7 As New MyColor(240, 240, 240)
    Public ColorGray8 As New MyColor(245, 245, 245)
    Public ColorSemiTransparent As New MyColor(1, Color8)

    Public ThemeNow As Integer = -1
    Public ColorHue As Integer = 210, ColorSat As Integer = 85, ColorLightAdjust As Integer = 0, ColorHueTopbarDelta As OneOf(Of Integer, Integer()) = 0
    Public ThemeDontClick As Integer = 0

    Public Sub ThemeRefresh(Optional NewTheme As Integer = -1)
        Try
            If ThemeNow = NewTheme AndAlso NewTheme >= 0 Then Return
            If NewTheme >= 0 Then ThemeNow = NewTheme

            Color1 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.2, 25 + ColorLightAdjust * 0.3)
            Color2 = New MyColor().FromHSL2(ColorHue, ColorSat, 45 + ColorLightAdjust)
            Color3 = New MyColor().FromHSL2(ColorHue, ColorSat, 55 + ColorLightAdjust)
            Color4 = New MyColor().FromHSL2(ColorHue, ColorSat, 65 + ColorLightAdjust)
            Color5 = New MyColor().FromHSL2(ColorHue, ColorSat, 80 + ColorLightAdjust * 0.4)
            Color6 = New MyColor().FromHSL2(ColorHue, ColorSat, 91 + ColorLightAdjust * 0.1)
            Color7 = New MyColor().FromHSL2(ColorHue, ColorSat, 95)
            Color8 = New MyColor().FromHSL2(ColorHue, ColorSat, 97)
            ColorBg0 = Color4 * 0.4 + Color5 * 0.4 + ColorGray4 * 0.2
            ColorBg1 = New MyColor(190, Color7)

            ColorSemiTransparent = New MyColor(1, Color8)
            Application.Current.Resources("ColorBrush1") = New SolidColorBrush(Color1)
            Application.Current.Resources("ColorBrush2") = New SolidColorBrush(Color2)
            Application.Current.Resources("ColorBrush3") = New SolidColorBrush(Color3)
            Application.Current.Resources("ColorBrush4") = New SolidColorBrush(Color4)
            Application.Current.Resources("ColorBrush5") = New SolidColorBrush(Color5)
            Application.Current.Resources("ColorBrush6") = New SolidColorBrush(Color6)
            Application.Current.Resources("ColorBrush7") = New SolidColorBrush(Color7)
            Application.Current.Resources("ColorBrush8") = New SolidColorBrush(Color8)
            Application.Current.Resources("ColorBrushBg0") = New SolidColorBrush(ColorBg0)
            Application.Current.Resources("ColorBrushBg1") = New SolidColorBrush(ColorBg1)
            Application.Current.Resources("ColorObject1") = CType(Color1, Color)
            Application.Current.Resources("ColorObject2") = CType(Color2, Color)
            Application.Current.Resources("ColorObject3") = CType(Color3, Color)
            Application.Current.Resources("ColorObject4") = CType(Color4, Color)
            Application.Current.Resources("ColorObject5") = CType(Color5, Color)
            Application.Current.Resources("ColorObject6") = CType(Color6, Color)
            Application.Current.Resources("ColorObject7") = CType(Color7, Color)
            Application.Current.Resources("ColorObject8") = CType(Color8, Color)
            Application.Current.Resources("ColorObjectBg0") = CType(ColorBg0, Color)
            Application.Current.Resources("ColorObjectBg1") = CType(ColorBg1, Color)
            ThemeRefreshMain()
        Catch ex As Exception
            Logger.Error(ex, "刷新主题颜色失败", LogBehavior.Toast)
        End Try
    End Sub
    Public Sub ThemeRefreshMain()
        RunInUi(
        Sub()
            If Not FrmMain.IsLoaded Then Return
            '顶部条背景
            Dim Brush = New LinearGradientBrush With {.EndPoint = New Point(1, 0), .StartPoint = New Point(0, 0)}
            Dim Deltas = ColorHueTopbarDelta.Switch(Function(d) New Integer() {-d, 0, d}, Function(d) d)
            Brush.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor().FromHSL2(ColorHue + Deltas(0), ColorSat, 48 + ColorLightAdjust)})
            Brush.GradientStops.Add(New GradientStop With {.Offset = 0.5, .Color = New MyColor().FromHSL2(ColorHue + Deltas(1), ColorSat, 54 + ColorLightAdjust)})
            Brush.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor().FromHSL2(ColorHue + Deltas(2), ColorSat, 48 + ColorLightAdjust)})
            FrmMain.PanTitle.Background = Brush
            FrmMain.PanTitle.Background.Freeze()
            '主页面背景
            If Settings.Get(Of Boolean)("UiBackgroundColorful") Then
                Brush = New LinearGradientBrush With {.EndPoint = New Point(0.1, 1), .StartPoint = New Point(0.9, 0)}
                Brush.GradientStops.Add(New GradientStop With {.Offset = -0.1, .Color = New MyColor().FromHSL2(ColorHue - 15, ColorSat * 0.8, 91)})
                Brush.GradientStops.Add(New GradientStop With {.Offset = 0.4, .Color = New MyColor().FromHSL2(ColorHue, ColorSat * 0.8, 91)})
                Brush.GradientStops.Add(New GradientStop With {.Offset = 1.1, .Color = New MyColor().FromHSL2(ColorHue + 15, ColorSat * 0.8, 91)})
                FrmMain.PanForm.Background = Brush
            Else
                FrmMain.PanForm.Background = New MyColor(245, 245, 245)
            End If
            FrmMain.PanForm.Background.Freeze()
        End Sub)
    End Sub
    Friend Sub ThemeCheckAll(EffectSetup As Boolean)
    End Sub
    Friend Function ThemeCheckOne(Id As Integer) As Boolean
        Return True
    End Function
    Friend Function ThemeUnlock(Id As Integer, Optional ShowDoubleHint As Boolean = True, Optional UnlockHint As String = Nothing) As Boolean
        Return False
    End Function

#End Region

#Region "更新"

    Friend Sub UpdateCheckByButton()
        Hint("该版本中不包含更新功能……")
    End Sub

    Friend IsUpdateWaitingRestart As Boolean = False
    Public Sub UpdateRestart(TriggerRestartAndByEnd As Boolean)
    End Sub
    Public Sub UpdateReplace(ProcessId As Integer, OldFileName As String, NewFileName As String, TriggerRestart As Boolean)
    End Sub

    ''' <summary>
    ''' 确保 PathTemp/Latest.exe 是最新正式版的 PCL，它会被用于整合包打包。
    ''' 如果不是，则下载一个。
    ''' </summary>
    Friend Sub DownloadLatestPCL(Optional LoaderToSyncProgress As LoaderBase = Nothing)
        '注意：如果要自行实现这个功能，请换用另一个文件路径，以免与官方版本冲突
    End Sub

#End Region

#Region "联网配置"

    ''' <summary>
    ''' 联网获取的配置信息。
    ''' 若获取失败或仍在获取中，可能为 Nothing。
    ''' </summary>
    Public ServerConfig As JObject

    Public ServerLoader As New LoaderTask(Of Integer, Integer)("PCL 配置更新", Sub() Logger.Info("该版本中不包含更新通知功能……"), Priority:=ThreadPriority.BelowNormal) With
        {.ReloadTimeout = 1000 * 60 * 60} '超时 1 小时

#End Region

#Region "赞助等级"

    Public ReadOnly Property CurrentRank As DonationRank
        Get
            Return DonationRank.None
        End Get
    End Property

    Public Sub InputPotatoCode(IsUpdating As Boolean)
    End Sub
    Friend Sub GeneratePotatoCode()
    End Sub

    ''' <summary>
    ''' 获取设备识别码。
    ''' </summary>
    Friend Function GetIdentify() As String
        Return "0000-0000-0000-0000"
    End Function

#End Region

End Module
