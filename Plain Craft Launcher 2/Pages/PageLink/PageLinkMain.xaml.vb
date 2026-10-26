Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Net
Imports System.Reflection
Imports System.Text.RegularExpressions
Imports PCL

''' <summary>
''' PCL3：联机页。
''' 上游这一页基于 EasyTier，但 EasyTier 官方的会合节点已经停止服务
''' （public / uptime 系列域名全部 NXDOMAIN，mc1.easytier.cn 也不再响应），
''' 没有会合点就没法让两个 NAT 后面的机器互相发现，所以整页改为基于 bore 隧道：
'''   · 房主：输入游戏端口 → 自动下载 bore.exe → 自动开隧道 → 得到 bore.pub:xxxxx
'''   · 客人：输入这串地址 → 提示去游戏里「直接连接」
''' bore 是单文件、免注册、免登录的公共隧道工具，只有房主需要运行。
''' </summary>
Public Class PageLinkMain

    '===============================
    '  状态机与前端页面
    '===============================

#Region "状态管理"

    Public Enum LinkStates
        Waiting
        Loading
        Failed
        Finished
    End Enum
    Public Shared LinkState As LinkStates = LinkStates.Waiting

    ''' <summary>
    ''' 切换到指定的状态。
    ''' </summary>
    Private Sub ChangeState(NewState As LinkStates)
        If LinkState = NewState Then Return
        Dim OldState = LinkState
        LinkState = NewState
        Logger.Info($"主状态由 {OldState} 变更为 {NewState}")
        RunInUi(
        Sub()
            Select Case NewState
                Case LinkStates.Waiting
                    SwitchToWaiting(OldState)
                Case LinkStates.Loading
                    SwitchToLoading(OldState)
                Case LinkStates.Failed
                    SwitchToFailed(OldState)
                Case LinkStates.Finished
                    SwitchToFinished(OldState)
            End Select
        End Sub)
    End Sub

    '让加载器的 成功/失败 事件触发状态切换
    Private Sub LoaderInit() Handles Me.Initialized
        PageLoaderInit(Load, PanLoad, PanContent, PanAlways, LinkLoader, AutoRun:=False)
        AddHandler LinkLoader.OnStateChangedUi,
        Sub(Loader As LoaderBase, NewState As LoadState, OldState As LoadState)
            Select Case NewState
                Case LoadState.Finished
                    ChangeState(LinkStates.Finished)
                Case LoadState.Failed
                    ChangeState(LinkStates.Failed)
            End Select
        End Sub
    End Sub

    '页面动画兼容
    Private Sub UpdatePanelVisibility() Handles Me.PageEnter
        FrmLinkMain.PanSelect.Visibility = If(LinkState = LinkStates.Waiting, Visibility.Visible, Visibility.Collapsed)
        FrmLinkMain.PanFinish.Visibility = If(LinkState = LinkStates.Finished, Visibility.Visible, Visibility.Collapsed)
    End Sub

#End Region

#Region "初始页面 (Waiting)"

    '由任意状态切换到 Waiting
    Private Sub SwitchToWaiting(OldState As LinkStates)
        LinkLoader.Cancel()
        SyncLock LinkLoader.LockState
            LinkLoader.State = LoadState.Waiting
        End SyncLock
        ProcessStop()
        TunnelAddress = Nothing
        If OldState <> LinkStates.Finished Then UpdatePanelVisibility()
        PageOnContentExit()
    End Sub

#End Region

#Region "创建 / 加入"

    '创建房间（房主）
    Private Sub Create_MouseLeftButtonUp() Handles PanSelectCreate.MouseLeftButtonUp
        Dim Port As String = MyMsgBoxInput("输入端口",
            $"在单人游戏的暂停菜单选择 {vbLQ}对局域网开放{vbRQ}，然后输入端口数字。{vbCrLf}输入后会自动建立一条公网隧道，把地址发给朋友就能连。",
            ValidateRules:=New Collection(Of Validate) From {New ValidateInteger(1024, 65535)},
            HintText:="端口号")
        If Port Is Nothing Then Return
        IsServerSide = True
        ServerPort = CInt(Port)
        TunnelAddress = Nothing
        Logger.Info($"尝试创建房间，端口 {ServerPort}")
        ChangeState(LinkStates.Loading)
    End Sub

    '加入房间（客人）
    Private Shared LastCode As String = Nothing
    Public Shared Sub Join() Handles PanSelectJoin.MouseLeftButtonUp
        Dim Code As String = MyMsgBoxInput("输入地址", "输入房主发给你的地址，形如 bore.pub:12345 或 [2604:a880:...]:12345。",
            HintText:=If(String.IsNullOrEmpty(LastCode), "", "使用上一次的地址"))
        If Not String.IsNullOrEmpty(LastCode) AndAlso Code IsNot Nothing AndAlso Code = "" Then Code = LastCode
        If Code Is Nothing Then Return
        Join(Code)
    End Sub

    Public Shared Sub Join(Code As String)
        If Code Is Nothing Then Return
        Dim Check = ValidateCodeFormat(Code)
        If Check IsNot Nothing Then
            Hint(Check, HintType.Red)
            Return
        End If
        LastCode = Code.Trim()
        '客人不需要开隧道，直接进完成页给出提示
        If FrmLinkMain Is Nothing Then Return
        FrmLinkMain.IsServerSide = False
        FrmLinkMain.TunnelAddress = LastCode
        RunInUi(Sub() FrmLinkMain.ChangeState(LinkStates.Finished))
    End Sub

    ''' <summary>
    ''' 校验地址格式。返回 Nothing 表示合法，否则返回错误提示。
    ''' </summary>
    Public Shared Function ValidateCodeFormat(Code As String) As String
        If Code Is Nothing Then Return "地址为空！"
        Code = Code.Trim()
        If Code.Length = 0 Then Return "地址为空！"
        '支持两种写法：bore.pub:12345 和 [2604:a880:...]:12345
        Dim SplitIndex As Integer = Code.LastIndexOf(":"c)
        If SplitIndex <= 0 OrElse SplitIndex = Code.Length - 1 Then Return "地址格式不对，应该是 主机:端口"
        Dim PortText As String = Code.Substring(SplitIndex + 1)
        Dim PortNumber As Integer
        If Not Integer.TryParse(PortText, PortNumber) OrElse PortNumber <= 0 OrElse PortNumber > 65535 Then
            Return "地址里的端口不对，应该是 1-65535 的数字"
        End If
        Return Nothing
    End Function

    '进入页面时自动尝试加入
    Private Shared AutoJoinCode As String = Nothing
    Private Sub PanSelectJoin_Loaded() Handles PanSelectJoin.Loaded
        If AutoJoinCode Is Nothing Then Return
        If LinkState = LinkStates.Waiting Then Join(AutoJoinCode)
        AutoJoinCode = Nothing
    End Sub

#End Region

#Region "加载页面 (Loading | Failed)"

    '由 Waiting 或 Failed 状态切换到 Loading
    Private Sub SwitchToLoading(OldState As LinkStates)
        FailReason = "准备初始化"
        LinkLoader.Start(IsForceRestart:=True)
        UpdateProgressBar(0)
        LabLoadTitle.Text = "创建房间中"
        UpdateLoadingPage("正在准备隧道程序……", "准备隧道程序")
    End Sub

    '由 Loading 状态切换到 Failed
    Private Sub SwitchToFailed(OldState As LinkStates)
        LinkLoader.Cancel()
        ProcessStop()
        UpdateProgressBar(1)
        LabLoadTitle.Text = FailReason
        Dim Brief As String = LinkLoader.Error.GetDisplay(False)
        Dim InnerEx As Exception = LinkLoader.Error
        If InnerEx.Message.StartsWithF("$") Then Brief = InnerEx.Message
        Do Until InnerEx.InnerException Is Nothing
            InnerEx = InnerEx.InnerException
            If InnerEx.Message.StartsWithF("$") Then Brief = InnerEx.Message
        Loop
        LabLoadDesc.Text = If(Brief.StartsWithF("$"), Brief.TrimStart("$"), LinkLoader.Error.GetDisplay(True))
        Logger.Warn(LinkLoader.Error, LabLoadTitle.Text)
    End Sub

    Private Sub UpdateLoadingPage(Title As String, FailBrief As String)
        If FailReason = FailBrief & "失败" Then Return
        FailReason = FailBrief & "失败"
        Logger.Info($"开始步骤：{Title}")
        RunInUiWait(
        Sub()
            If FrmLinkMain Is Nothing OrElse Not FrmLinkMain.LabLoadDesc.IsLoaded Then Return
            FrmLinkMain.LabLoadDesc.Text = Title
        End Sub)
    End Sub
    Private FailReason As String = "准备初始化"

    '取消加载
    Private Sub BtnLoadCancel_Click() Handles BtnLoadCancel.Click
        ChangeState(LinkStates.Waiting)
    End Sub

    '点击重试
    Private Sub CardLoad_MouseLeftButtonUp() Handles CardLoad.MouseLeftButtonUp
        ChangeState(LinkStates.Loading)
    End Sub

    '进度条
    Private Sub UpdateProgressBar(Optional Value As Double = -1)
        If Value = -1 Then Value = LinkLoader.Progress
        Dim DisplayingProgress As Double = ColumnProgressA.Width.Value
        If Math.Round(Value - DisplayingProgress, 3) = 0 Then Return
        If DisplayingProgress <= Value Then
            Dim NewProgress As Double = If(Value = 1, 1, DisplayingProgress + (Value - DisplayingProgress) ^ 2 * 0.5)
            AniStart({
                AaGridLengthWidth(ColumnProgressA, NewProgress - ColumnProgressA.Width.Value, 200),
                AaGridLengthWidth(ColumnProgressB, (1 - NewProgress) - ColumnProgressB.Width.Value, 200)
            }, "Link Progress")
        Else
            ColumnProgressA.Width = New GridLength(Value, GridUnitType.Star)
            ColumnProgressB.Width = New GridLength(1 - Value, GridUnitType.Star)
            AniStop("Link Progress")
        End If
    End Sub
    Private Sub CardLoad_SizeChanged() Handles CardLoad.SizeChanged
        RectProgressClip.Rect = New Rect(0, 0, CardLoad.ActualWidth, 12)
    End Sub

#End Region

#Region "完成页面 (Finished)"

    '由 Loading 状态切换到 Finished
    Private Sub SwitchToFinished(OldState As LinkStates)
        UpdatePanelVisibility()
        '不需要的统计信息一律隐藏
        BtnFinishPing.Visibility = Visibility.Collapsed
        BtnFinishPlayer.Visibility = Visibility.Collapsed
        If IsServerSide Then
            LabFinishTitle.Text = "房间已创建"
            LabFinishDesc.Text = $"把这串地址发给朋友：{vbCrLf}{TunnelAddress}{vbCrLf}{vbCrLf}" &
                $"让他在游戏里「多人游戏 → 直接连接」填进去。{vbCrLf}关掉这个页面隧道就断了。"
            BtnFinishExit.Text = "关闭房间"
            BtnFinishCopy.Visibility = Visibility.Visible
            BtnFinishCopy.Text = "复制地址"
            '下边栏显示游戏端口
            LineFinishPort.Visibility = Visibility.Visible
            BtnFinishPort.Visibility = Visibility.Visible
            LabFinishPort.Visibility = Visibility.Visible
            LabFinishPort.Text = ServerPort.ToString()
            BtnFinishIp.Visibility = Visibility.Collapsed
            LabFinishIp.Visibility = Visibility.Collapsed
            Copy()
        Else
            LabFinishTitle.Text = "请在游戏里连接"
            LabFinishDesc.Text = $"打开游戏 →「多人游戏」→「直接连接」，填入下面这串地址：{vbCrLf}{TunnelAddress}"
            BtnFinishExit.Text = "返回"
            BtnFinishCopy.Visibility = Visibility.Visible
            BtnFinishCopy.Text = "复制地址"
            LineFinishPort.Visibility = Visibility.Collapsed
            BtnFinishPort.Visibility = Visibility.Collapsed
            LabFinishPort.Visibility = Visibility.Collapsed
            LabFinishIp.Text = TunnelAddress
            BtnFinishIp.Visibility = Visibility.Visible
            LabFinishIp.Visibility = Visibility.Visible
        End If
    End Sub

    '退出
    Private Sub BtnFinishExit_Click() Handles BtnFinishExit.Click
        TryExit(False, False)
    End Sub

    ''' <summary>
    ''' 退出联机。返回是否弹出了警告窗口并且玩家选择了取消。
    ''' </summary>
    Public Function TryExit(Silent As Boolean, Closing As Boolean) As Boolean
        If LinkState = LinkStates.Waiting OrElse LinkState = LinkStates.Failed Then Return False
        If Not Silent AndAlso Closing AndAlso IsServerSide Then
            If MyMsgBox("关闭后朋友就连不上了，确定吗？", "关闭房间", "确定", "取消") = 2 Then Return True
        End If
        ChangeState(LinkStates.Waiting)
        Return False
    End Function

    '复制地址
    Private Sub Copy() Handles BtnFinishCopy.Click
        ClipboardSet(TunnelAddress, False)
        Hint("已复制地址！", HintType.Green)
    End Sub

    '复制地址（客人页的地址按钮）
    Private Sub BtnFinishIp_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs) Handles BtnFinishIp.MouseLeftButtonUp
        ClipboardSet(TunnelAddress, False)
        Hint("已复制地址！", HintType.Green)
    End Sub

#End Region

    '===============================
    '  后端：bore 隧道
    '===============================

#Region "字段"

    Private IsServerSide As Boolean
    Private ServerPort As Integer
    ''' <summary>
    ''' 隧道地址，形如 bore.pub:12345。
    ''' </summary>
    Private TunnelAddress As String = Nothing

    Private ReadOnly PathBoreDir As String = Paths.AppDataThenName & "Bore\"
    Private Const BoreVersion As String = "0.6.0"
    Private ReadOnly BoreFileName As String = $"bore-v{BoreVersion}-x86_64-pc-windows-msvc.zip"

#End Region

#Region "加载器"

    Private WithEvents LinkLoader As New LoaderCombo(Of Integer)("联机", {
        New LoaderTask(Of Integer, Integer)("准备隧道程序", AddressOf PrepareBore) With {.ProgressWeight = 30},
        New LoaderTask(Of Integer, Integer)("建立隧道", AddressOf StartTunnel) With {.ProgressWeight = 70}
    })

    ''' <summary>
    ''' 确保 bore.exe 存在，没有就下载。
    ''' </summary>
    Private Sub PrepareBore(Task As LoaderTask(Of Integer, Integer))
        UpdateLoadingPage("正在检查隧道程序……", "检查隧道程序")
        Directory.CreateDirectory(PathBoreDir)
        Dim ExePath As String = PathBoreDir & "bore.exe"
        If FileUtils.Exists(ExePath) Then
            Logger.Info("隧道程序已存在，跳过下载")
            Return
        End If
        Dim Urls As String() = {
            $"https://ghproxy.net/https://github.com/ekzhang/bore/releases/download/v{BoreVersion}/{BoreFileName}",
            $"https://gh-proxy.com/https://github.com/ekzhang/bore/releases/download/v{BoreVersion}/{BoreFileName}",
            $"https://github.com/ekzhang/bore/releases/download/v{BoreVersion}/{BoreFileName}"
        }
        Dim ZipPath As String = PathBoreDir & "bore.zip"
        Dim LastError As Exception = Nothing
        For Each Url As String In Urls
            Try
                UpdateLoadingPage("正在下载隧道程序……", "下载隧道程序")
                Logger.Info($"下载隧道程序：{Url}")
                Using Client As New WebClient()
                    Client.Headers.Add("User-Agent", "PCL3")
                    Client.DownloadFile(Url, ZipPath)
                End Using
                If Not FileUtils.Exists(ZipPath) Then Throw New Exception("下载后没有找到文件")
                UpdateLoadingPage("正在解压隧道程序……", "解压隧道程序")
                FileUtils.ExtractToDirectory(ZipPath, PathBoreDir)
                FileUtils.Delete(ZipPath)
                If Not FileUtils.Exists(ExePath) Then Throw New Exception("解压后没有找到 bore.exe")
                Logger.Info("隧道程序准备完成")
                Return
            Catch ex As Exception
                LastError = ex
                Logger.Warn(ex, $"下载失败，换下一个源：{Url}")
            End Try
        Next
        Throw New Exception($"$下载隧道程序失败，请检查网络后重试。{vbCrLf}最后一条错误：{LastError?.Message}")
    End Sub

    ''' <summary>
    ''' 启动 bore 并等待它分配公网端口。
    ''' </summary>

    ''' <summary>
    ''' 探测 bore.pub 的哪个协议可达，返回可以直接传给 --to 的地址。
    ''' 优先 IPv4（绝大多数网络正常），不通再试 IPv6。
    ''' </summary>
    Private Function ResolveBoreServer() As String
        For Each Family As Net.Sockets.AddressFamily In {Net.Sockets.AddressFamily.InterNetwork, Net.Sockets.AddressFamily.InterNetworkV6}
            Try
                For Each Addr As Net.IPAddress In Net.Dns.GetHostAddresses("bore.pub")
                    If Addr.AddressFamily <> Family Then Continue For
                    If Not CanConnect(Addr, 7835) Then Continue For
                    Dim Text As String = Addr.ToString()
                    If Family = Net.Sockets.AddressFamily.InterNetworkV6 Then Text = $"[{Text}]"
                    Logger.Info($"bore 服务端可达：{Text}:7835")
                    Return $"{Text}:7835"
                Next
            Catch ex As Exception
                Logger.Warn(ex, $"探测 bore.pub 的 {Family} 地址失败")
            End Try
        Next
        Logger.Warn("bore.pub 的 IPv4 与 IPv6 都探测不通")
        Return ""
    End Function

    ''' <summary>
    ''' 用 TCP 试探地址是否可达，超时 3 秒。
    ''' </summary>
    Private Function CanConnect(Addr As Net.IPAddress, Port As Integer) As Boolean
        Try
            Using Client As New Net.Sockets.TcpClient(Addr.AddressFamily)
                Dim TaskConnect = Client.ConnectAsync(Addr, Port)
                Return TaskConnect.Wait(3000) AndAlso Client.Connected
            End Using
        Catch
            Return False
        End Try
    End Function
    Private Sub StartTunnel(Task As LoaderTask(Of Integer, Integer))
        UpdateLoadingPage("正在建立隧道……", "建立隧道")
        Dim ExePath As String = PathBoreDir & "bore.exe"
        'bore.pub 的 IPv4 在部分线路上不可达（TCP 直接超时），这里先探测哪个协议能通
        Dim ServerTarget As String = ResolveBoreServer()
        If String.IsNullOrWhiteSpace(ServerTarget) Then
            Throw New Exception("$连不上联机服务器 bore.pub。" & vbCrLf &
                                "它的 IPv4 在国内部分线路被封，需要你的网络支持 IPv6。" & vbCrLf &
                                "打开 https://test-ipv6.com/ 确认一下，没有 IPv6 就在光猫/路由器里开启。")
        End If
        Dim Info As New ProcessStartInfo With {
            .FileName = ExePath,
            .Arguments = $"local {ServerPort} --to {ServerTarget}",
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }
        Logger.Info($"启动隧道：bore local {ServerPort} --to {ServerTarget}")
        ProcessCore = New Process With {.StartInfo = Info}
        ProcessCore.Start()
        'bore 的日志走 stdout（tracing 默认），stderr 通常是空的。
        '两个流都接上，免得以后换版本又把输出丢了。
        AttachReader(ProcessCore.StandardOutput)
        AttachReader(ProcessCore.StandardError)
        Dim Deadline As DateTime = Now.AddSeconds(30)
        Do While Now < Deadline
            If Task.IsCanceled Then Throw New OperationCanceledException
            Thread.Sleep(200)
            If ProcessCore.HasExited Then
                Throw New Exception($"$隧道程序意外退出（代码 {ProcessCore.ExitCode}）。{vbCrLf}近期输出：{vbCrLf}{BoreLogText()}")
            End If
            Dim LogText As String = BoreLogText()
            'bore 连上后一定会打印 remote_port=xxxxx，这条最可靠；
            'IPv6 形式下 listening 行是 [地址]:7835:端口，所以不能只认 bore.pub:
            Dim Match = Regex.Match(LogText, "remote_port=(\d+)")
            If Not Match.Success Then Match = Regex.Match(LogText, "bore\.pub:(\d+)")
            If Not Match.Success Then Match = Regex.Match(LogText, ":([0-9]{4,5})\s*$", RegexOptions.Multiline)
            If Match.Success Then
                '给朋友发实际连通的地址：bore.pub 的 IPv4 被封，发域名他解析到 IPv4 就卡住了
                Dim HostPart As String = ServerTarget.Substring(0, ServerTarget.LastIndexOf(":"c))
                TunnelAddress = $"{HostPart}:{Match.Groups(1).Value}"
                Logger.Info($"隧道已建立：{TunnelAddress}")
                Task.Progress = 1
                Return
            End If
            Task.Progress = 0.3 + Math.Min(0.6, (30 - (Deadline - Now).TotalSeconds) / 30 * 0.6)
        Loop
        Throw New Exception($"$等待隧道超时。{vbCrLf}近期输出：{vbCrLf}{BoreLogText()}")
    End Sub

#End Region

#Region "进程与日志"

    Private Shared ProcessCore As Process
    Private Shared BoreLog As New Text.StringBuilder

    Private Sub AttachReader(Reader As IO.StreamReader)
        Dim Worker As New Threading.Thread(
        Sub()
            Try
                Do
                    Dim Line As String = Reader.ReadLine()
                    If Line Is Nothing Then Exit Do
                    'bore 会往输出里塞 ANSI 颜色控制字符（如 ESC[0m），
                    '不清掉的话 "remote_port=12345" 中间会夹着转义序列，正则永远匹配不到
                    Line = Regex.Replace(Line, ChrW(27) & "\[[0-9;]*[A-Za-z]", "")
                    SyncLock BoreLog
                        BoreLog.AppendLine(Line)
                        If BoreLog.Length > 8192 Then BoreLog.Remove(0, 4096)
                    End SyncLock
                Loop
            Catch
                '读取结束，忽略
            End Try
        End Sub) With {.IsBackground = True}
        Worker.Start()
    End Sub

    Private Shared Function BoreLogText() As String
        SyncLock BoreLog
            Return BoreLog.ToString().Trim()
        End SyncLock
    End Function

    ''' <summary>
    ''' 结束隧道进程。即使失败也不会抛出异常。
    ''' </summary>
    Public Shared Sub ProcessStop()
        Try
            If ProcessCore IsNot Nothing AndAlso Not ProcessCore.HasExited Then
                Logger.Info($"关闭隧道进程，PID：{ProcessCore.Id}")
                ProcessCore.Kill()
            End If
        Catch ex As Exception
            Logger.Warn(ex, "关闭隧道进程失败")
        Finally
            ProcessCore = Nothing
            SyncLock BoreLog
                BoreLog.Clear()
            End SyncLock
        End Try
    End Sub

#End Region

End Class
