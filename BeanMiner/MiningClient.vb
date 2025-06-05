' In MiningClient.vb

Imports System.IO
Imports System.Net.Sockets
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Globalization ' For CultureInfo in ToString
Imports System.Collections.Concurrent ' For BlockingCollection

Public Class MiningClient

    Private _serverAddress As String
    Private _serverPort As Integer
    Private _client As TcpClient
    Private _currentJobDifficulty As Integer ' Difficulty for the current mining job
    Private _minerAddress As String ' The public key of this miner

    Private _stopClient As Boolean = False ' Master stop flag for all threads
    Private _primaryReaderThread As Thread
    Private _miningWorkerThread As Thread
    Private _hashRateDisplayThread As Thread

    Private _serverStreamWriter As StreamWriter
    Private _writerLock As New Object()

    Private _workPackageQueue As New BlockingCollection(Of JObject)(1)
    Private _currentMiningJobCts As CancellationTokenSource

    Private _hashCount As Long = 0
    Private _lastHashCountForRate As Long = 0
    Private _lastHashRateUpdateTime As DateTime = DateTime.UtcNow
    Private _currentHashRate As Double = 0.0

    Private _consoleLock As New Object()
    Private _currentStatusMessage As String = ""
    Private _currentMiningTargetInfo As String = "Waiting for work..."

    Public Sub New(serverAddress As String, serverPort As Integer, minerAddress As String)
        _serverAddress = serverAddress
        _serverPort = serverPort
        _minerAddress = minerAddress
    End Sub

    Public Sub Start()
        _stopClient = False
        UpdateConsoleDisplay("Start() method entered.", ConsoleColor.White, False, True) ' No clear for first message
        Try
            _client = New TcpClient()
            UpdateConsoleDisplay("TcpClient instantiated. Attempting Connect...", ConsoleColor.Yellow)

            _client.Connect(_serverAddress, _serverPort)
            UpdateConsoleDisplay($"Connect call completed. Client.Connected: {_client.Connected}", ConsoleColor.DarkGreen)

            If Not _client.Connected Then
                UpdateConsoleDisplay($"Failed to connect after Connect call (Connected is False). Server: {_serverAddress}:{_serverPort}", ConsoleColor.Red, True)
                Return
            End If

            UpdateConsoleDisplay("Attempting _client.GetStream()...", ConsoleColor.DarkYellow)
            Dim stream = _client.GetStream()
            UpdateConsoleDisplay("Got stream. Attempting New StreamWriter...", ConsoleColor.DarkYellow)
            _serverStreamWriter = New StreamWriter(stream) With {.AutoFlush = True}
            UpdateConsoleDisplay("StreamWriter initialized.", ConsoleColor.DarkGreen)

            UpdateConsoleDisplay($"Successfully connected to server: {_serverAddress}:{_serverPort}", ConsoleColor.Green, True) ' Important, clear old status

            SendInitialHandshake()

            UpdateConsoleDisplay("Attempting to start PrimaryReaderThread...", ConsoleColor.White)
            _primaryReaderThread = New Thread(AddressOf PrimaryServerMessageReaderLoop)
            _primaryReaderThread.IsBackground = True
            _primaryReaderThread.Start()
            UpdateConsoleDisplay("PrimaryReaderThread started.", ConsoleColor.Green)

            UpdateConsoleDisplay("Attempting to start MiningWorkerThread...", ConsoleColor.White)
            _miningWorkerThread = New Thread(AddressOf MiningWorkerLoop)
            _miningWorkerThread.IsBackground = True
            _miningWorkerThread.Start()
            UpdateConsoleDisplay("MiningWorkerThread started.", ConsoleColor.Green)

            UpdateConsoleDisplay("Attempting to start HashRateDisplayThread...", ConsoleColor.White)
            _hashRateDisplayThread = New Thread(AddressOf CalculateAndDisplayHashRateLoop)
            _hashRateDisplayThread.IsBackground = True
            _hashRateDisplayThread.Start()
            UpdateConsoleDisplay("HashRateDisplayThread started. Start() method finished.", ConsoleColor.Green)

        Catch sockEx As SocketException
            UpdateConsoleDisplay($"SocketException in Start(): {sockEx.Message} (ErrorCode: {sockEx.SocketErrorCode})", ConsoleColor.Red, True)
            UpdateConsoleDisplay($"Full SocketException: {sockEx.ToString()}", ConsoleColor.DarkRed, False, True)
            _stopClient = True
        Catch ex As Exception
            UpdateConsoleDisplay($"Generic Exception in Start(): {ex.Message}", ConsoleColor.Red, True)
            UpdateConsoleDisplay($"Full Generic Exception: {ex.ToString()}", ConsoleColor.DarkRed, False, True)
            _stopClient = True
        End Try
    End Sub

    Private Sub SendInitialHandshake()
        UpdateConsoleDisplay("SendInitialHandshake() entered.", ConsoleColor.White)
        Try
            Dim handshakeMsg = New JObject From {{"minerAddress", _minerAddress}}
            Dim jsonToSend = handshakeMsg.ToString(Formatting.None)
            UpdateConsoleDisplay($"Handshake JSON prepared: {jsonToSend.Substring(0, Math.Min(jsonToSend.Length, 50))}...", ConsoleColor.DarkGray)

            SyncLock _writerLock
                If _serverStreamWriter Is Nothing Then
                    UpdateConsoleDisplay("ERROR: _serverStreamWriter is null in SendInitialHandshake!", ConsoleColor.Red, True)
                    Return
                End If
                UpdateConsoleDisplay("Attempting _serverStreamWriter.WriteLine for handshake...", ConsoleColor.Yellow)
                _serverStreamWriter.WriteLine(jsonToSend)
                UpdateConsoleDisplay("_serverStreamWriter.WriteLine for handshake completed.", ConsoleColor.DarkGreen)
            End SyncLock

            UpdateConsoleDisplay($"Sent handshake. Miner: {_minerAddress.Substring(0, Math.Min(10, _minerAddress.Length))}...", ConsoleColor.Cyan)
        Catch ex As Exception
            UpdateConsoleDisplay($"CRITICAL ERROR in SendInitialHandshake: {ex.ToString()}", ConsoleColor.DarkRed, True)
            _stopClient = True
        End Try
    End Sub
    Private Sub PrimaryServerMessageReaderLoop()
        If _client Is Nothing OrElse Not _client.Connected Then Return

        Dim stream As NetworkStream = Nothing
        Dim reader As StreamReader = Nothing
        Try
            stream = _client.GetStream()
            reader = New StreamReader(stream)

            While Not _stopClient AndAlso _client.Connected
                Dim serverMessageJson As String = reader.ReadLine()

                If String.IsNullOrEmpty(serverMessageJson) Then
                    If Not _client.Connected Or _stopClient Then Exit While
                    Thread.Sleep(100)
                    Continue While
                End If

                Dim serverMessage As JObject
                Try
                    serverMessage = JObject.Parse(serverMessageJson)
                Catch exJson As JsonReaderException
                    UpdateConsoleDisplay($"Error parsing server JSON: {exJson.Message}. Received: {serverMessageJson}", ConsoleColor.DarkRed)
                    Continue While
                End Try

                If IsWorkPackage(serverMessage) Then
                    UpdateConsoleDisplay("Received new work package from server.", ConsoleColor.Magenta)
                    _currentMiningJobCts?.Cancel() ' Cancel previous job
                    _workPackageQueue.Add(serverMessage) ' Add new job
                Else
                    HandleServerNotificationOrStatus(serverMessage, serverMessageJson)
                End If
            End While
        Catch exIO As IOException
            If Not _stopClient Then UpdateConsoleDisplay($"Reader IO Error (server disconnected?): {exIO.Message}", ConsoleColor.Red)
        Catch exSocket As SocketException
            If Not _stopClient Then UpdateConsoleDisplay($"Reader Socket Error (server disconnected?): {exSocket.Message}", ConsoleColor.Red)
        Catch exThread As ThreadInterruptedException
            UpdateConsoleDisplay("Primary message reader thread interrupted.", ConsoleColor.Yellow) ' Expected on Kill
        Catch ex As Exception
            If Not _stopClient Then UpdateConsoleDisplay($"Critical error in primary message reader: {ex.ToString()}", ConsoleColor.Red)
        Finally
            _stopClient = True ' Ensure client stops if reader fails
            _currentMiningJobCts?.Cancel() ' Cancel any ongoing mining
            _workPackageQueue.CompleteAdding() ' Signal no more work will be added
            UpdateConsoleDisplay("Primary message reader stopped.", ConsoleColor.Gray, False, True)
        End Try
    End Sub

    Private Function IsWorkPackage(message As JObject) As Boolean
        Return message.ContainsKey("lastIndex") AndAlso message.ContainsKey("lastHash") AndAlso
               message.ContainsKey("difficulty") AndAlso message.ContainsKey("rewardAmount") AndAlso
               message.ContainsKey("mempool")
    End Function

    Private Sub HandleServerNotificationOrStatus(serverMessage As JObject, rawJson As String)
        Dim msgType = serverMessage("type")?.ToString()
        Dim status = serverMessage("status")?.ToString()
        Dim message = serverMessage("message")?.ToString()

        If msgType = "newBlock" Then
            UpdateConsoleDisplay($"Server: New block {serverMessage("index")} (Hash: {serverMessage("hash").ToString().Substring(0, 8)}...) by {serverMessage("minerAddress")?.ToString().Substring(0, 10)}...", ConsoleColor.DarkMagenta)
            _currentMiningJobCts?.Cancel() ' New block from another miner, current work is stale
        ElseIf status IsNot Nothing Then
            If status.Equals("success", StringComparison.OrdinalIgnoreCase) Then
                UpdateConsoleDisplay($"Server ACK: {message} (BlockHash: {serverMessage("blockHash")?.ToString().Substring(0, 8)}...)", ConsoleColor.Green, True)
            ElseIf status.Equals("error", StringComparison.OrdinalIgnoreCase) Then
                UpdateConsoleDisplay($"Server NACK: {message}", ConsoleColor.Red, True)
                ' If server rejected our block (stale or invalid), we should also consider current work stale
                If message?.Contains("Stale block", StringComparison.OrdinalIgnoreCase) = True OrElse
                   message?.Contains("Invalid block", StringComparison.OrdinalIgnoreCase) = True Then
                    _currentMiningJobCts?.Cancel()
                End If
            Else
                UpdateConsoleDisplay($"Server Status Message: {rawJson}", ConsoleColor.Blue)
            End If
        Else
            UpdateConsoleDisplay($"Unknown Server Message (not work, not status): {rawJson}", ConsoleColor.Blue)
        End If
    End Sub
    Private Sub MiningWorkerLoop()
        Try
            While Not _stopClient
                _currentMiningJobCts?.Dispose() ' Dispose the previous job's CTS if it exists
                _currentMiningJobCts = New CancellationTokenSource() ' Create a new one for this job
                Dim currentJobToken As CancellationToken = _currentMiningJobCts.Token

                Dim workPackage As JObject = Nothing
                Try
                    ' Wait indefinitely for work, but respect cancellation token
                    workPackage = _workPackageQueue.Take(currentJobToken)
                Catch Ex As OperationCanceledException
                    ' This is expected if a new work package arrives or client is stopping
                    ' UpdateConsoleDisplay("Mining worker cancelled while waiting for work.", ConsoleColor.DarkYellow) ' Optional log
                    If _stopClient Then Exit While Else Continue While ' If client stopping, exit. Else, get new work.
                Catch ex As InvalidOperationException
                    ' This happens if _workPackageQueue is marked as complete and Take is called.
                    UpdateConsoleDisplay("Work package queue completed. Mining worker exiting.", ConsoleColor.DarkYellow)
                    Exit While ' Exit loop as no more work will come.
                End Try

                If _stopClient OrElse workPackage Is Nothing Then Exit While
                If currentJobToken.IsCancellationRequested Then Continue While ' New work arrived while processing Take

                Dim lastIndex As Integer = workPackage("lastIndex").ToObject(Of Integer)()
                Dim lastHash As String = workPackage("lastHash").ToString()
                _currentJobDifficulty = workPackage("difficulty").ToObject(Of Integer)() ' Store job's difficulty
                Dim rewardAmount As Decimal = workPackage("rewardAmount").ToObject(Of Decimal)()
                Dim minerAddressForReward As String = workPackage("minerAddressForReward").ToString()
                Dim mempoolTransactions As List(Of JObject) = workPackage("mempool").ToObject(Of List(Of JObject))()

                _currentMiningTargetInfo = $"Block {lastIndex + 1}, Diff: {_currentJobDifficulty}, Prev: {lastHash.Substring(0, 8)}"
                UpdateConsoleDisplay($"Starting new mining job. Target: {_currentMiningTargetInfo}, Txs: {mempoolTransactions.Count}, Reward: {rewardAmount} BEAN", ConsoleColor.Cyan)

                If rewardAmount <= 0 AndAlso mempoolTransactions.Count = 0 Then
                    UpdateConsoleDisplay("No reward and no transactions. Waiting for new work...", ConsoleColor.DarkYellow)
                    Continue While ' Effectively skips mining this "empty" work
                End If

                Dim blockTransactions As New List(Of JObject)(mempoolTransactions) ' Start with mempool txs

                Dim blockDateTimeObject = DateTime.UtcNow
                Dim fullPreciseTimestampString = blockDateTimeObject.ToUniversalTime().ToString(Block.PreciseTimestampFormat, CultureInfo.InvariantCulture)
                Dim serverLikeTimestampString As String

                If fullPreciseTimestampString.EndsWith("0Z") AndAlso fullPreciseTimestampString.Length > 3 AndAlso Char.IsDigit(fullPreciseTimestampString(fullPreciseTimestampString.Length - 3)) Then
                    Dim tempDt = DateTime.ParseExact(fullPreciseTimestampString, Block.PreciseTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    serverLikeTimestampString = tempDt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFZ", CultureInfo.InvariantCulture)
                Else
                    serverLikeTimestampString = fullPreciseTimestampString
                End If

                If rewardAmount > 0 Then
                    Dim coinbaseTxData = New JObject()
                    coinbaseTxData.Add("timestamp", New JValue(serverLikeTimestampString))
                    coinbaseTxData.Add("type", New JValue("transfer"))
                    coinbaseTxData.Add("from", New JValue("miningReward"))
                    coinbaseTxData.Add("to", New JValue(minerAddressForReward))
                    coinbaseTxData.Add("amount", New JValue(rewardAmount))
                    coinbaseTxData.Add("token", New JValue("BEAN"))
                    coinbaseTxData.Add("txId", New JValue(Guid.NewGuid().ToString("N")))
                    blockTransactions.Add(New JObject From {{"transaction", coinbaseTxData}})
                End If

                ' Create block with the specific difficulty for this job and the captured DateTime object
                ' Note: newBlockCandidate.Nonce will be Long as per Block.vb update
                Dim newBlockCandidate As New Block(lastIndex + 1, blockDateTimeObject, blockTransactions, lastHash, _currentJobDifficulty)
                Dim blockFound As Boolean = False
                Dim winningNonce As Long = -1L ' <<<< CHANGED TO Long, and -1L
                Dim winningHash As String = ""

                Interlocked.Exchange(_hashCount, 0L) ' Reset hash counter for this job (0L for Long)

                Dim numThreadsToUse = Math.Max(1, Environment.ProcessorCount - 1)
                If Environment.ProcessorCount = 1 Then numThreadsToUse = 1

                Dim parallelOptions As New ParallelOptions() With {
                .CancellationToken = currentJobToken,
                .MaxDegreeOfParallelism = numThreadsToUse
            }

                Try
                    Parallel.For(0, numThreadsToUse, parallelOptions,
                    Sub(coreIndex, loopState)
                        If currentJobToken.IsCancellationRequested OrElse _stopClient OrElse blockFound Then
                            loopState.Stop()
                            Return
                        End If

                        Dim threadBlock As New Block(newBlockCandidate.Index, newBlockCandidate.Timestamp,
                                                    newBlockCandidate.Data, newBlockCandidate.PreviousHash,
                                                    newBlockCandidate.Difficulty)

                        Dim currentNonce As Long = CLng(coreIndex) ' <<<< CHANGED TO Long

                        ' Loop up to MAX_NONCE_VALUE_UINT32_EQUIVALENT
                        While Not currentJobToken.IsCancellationRequested AndAlso Not _stopClient AndAlso Not blockFound AndAlso currentNonce <= Block.MAX_NONCE_VALUE_UINT32_EQUIVALENT
                            threadBlock.Nonce = currentNonce ' <<<< Assign Long to Block.Nonce (which is Long)
                            threadBlock.Hash = threadBlock.CalculateHash()
                            Interlocked.Increment(CType(Me, MiningClient)._hashCount)

                            If threadBlock.Hash.StartsWith(New String("0"c, threadBlock.Difficulty)) Then
                                ' Attempt to set winningNonce only if it hasn't been set yet (-1L)
                                If Interlocked.CompareExchange(winningNonce, currentNonce, -1L) = -1L Then ' <<<< CHANGED TO -1L
                                    winningHash = threadBlock.Hash
                                    blockFound = True
                                End If
                                loopState.Stop()
                                Exit While
                            End If
                            currentNonce += numThreadsToUse
                            ' No need to check for Long overflow within MAX_NONCE_VALUE_UINT32_EQUIVALENT range with typical numThreadsToUse
                        End While
                    End Sub)
                Catch exOpCancel As OperationCanceledException
                    UpdateConsoleDisplay("Mining job (Parallel.For) was cooperatively canceled.", ConsoleColor.Yellow)
                End Try

                If blockFound AndAlso Not _stopClient AndAlso Not currentJobToken.IsCancellationRequested Then
                    newBlockCandidate.Nonce = winningNonce ' <<<< Assign winning Long nonce
                    newBlockCandidate.Hash = winningHash
                    newBlockCandidate.BlockSize = newBlockCandidate.CalculateBlockSize()

                    UpdateConsoleDisplay($"MINED BLOCK! Nonce: {winningNonce}, Hash: {winningHash} (Diff: {newBlockCandidate.Difficulty})", ConsoleColor.Green, True)

                    Dim finalClientHashCheck = newBlockCandidate.CalculateHash()
                    Dim clientDataToHash = Block.LastCalculatedDataToHash ' For debugging purposes

                    If finalClientHashCheck <> winningHash Then
                        UpdateConsoleDisplay($"CRITICAL CLIENT HASH MISMATCH! Winning: {winningHash}, Recalc: {finalClientHashCheck}", ConsoleColor.DarkMagenta, True)
                        UpdateConsoleDisplay($"Client Data Hashed: '{clientDataToHash}'", ConsoleColor.DarkMagenta, False, True)
                    End If

                    Dim blockJsonString = JsonConvert.SerializeObject(newBlockCandidate, Formatting.None)
                    Dim blockJObject = JObject.Parse(blockJsonString)
                    Dim submission As New JObject()
                    submission.Add("block", blockJObject)
                    SyncLock _writerLock
                        If Not _stopClient AndAlso _serverStreamWriter IsNot Nothing AndAlso _client.Connected Then
                            Try
                                _serverStreamWriter.WriteLine(submission.ToString(Formatting.None))
                            Catch ex As Exception
                                UpdateConsoleDisplay($"Error sending mined block: {ex.Message}", ConsoleColor.Red)
                                _stopClient = True
                            End Try
                        End If
                    End SyncLock
                    If Not _stopClient Then UpdateConsoleDisplay("Sent mined block to server.", ConsoleColor.Cyan)
                ElseIf Not _stopClient Then
                    If currentJobToken.IsCancellationRequested Then
                        UpdateConsoleDisplay("Current mining job stopped due to new work/block or client shutdown.", ConsoleColor.Yellow)
                    Else
                        UpdateConsoleDisplay($"Mining attempt finished for diff {_currentJobDifficulty} (up to nonce {Block.MAX_NONCE_VALUE_UINT32_EQUIVALENT}), no block found.", ConsoleColor.DarkYellow)
                    End If
                End If
            End While
        Catch exOuterLoop As Exception
            If Not _stopClient Then UpdateConsoleDisplay($"Critical error in mining worker main loop: {exOuterLoop.ToString()}", ConsoleColor.Red)
        Finally
            _currentMiningJobCts?.Cancel()
            _currentMiningJobCts?.Dispose()
            _stopClient = True
            UpdateConsoleDisplay("Mining worker loop stopped.", ConsoleColor.Gray, False, True)
        End Try
    End Sub

    Private Sub CalculateAndDisplayHashRateLoop()
        Try
            While Not _stopClient
                Thread.Sleep(1000) ' Update every second
                If _stopClient Then Exit While

                Dim currentHashes = Interlocked.Read(_hashCount) ' Read the volatile hashCount
                Dim timeDiff = (DateTime.UtcNow - _lastHashRateUpdateTime).TotalSeconds

                If timeDiff >= 0.95 Then ' Check roughly every second, allow for small timing variations
                    _currentHashRate = If(timeDiff > 0, (currentHashes - _lastHashCountForRate) / timeDiff, 0)
                    _lastHashCountForRate = currentHashes ' Update for next calculation
                    _lastHashRateUpdateTime = DateTime.UtcNow
                    UpdateConsoleDisplay("", ConsoleColor.White, False, True, True) ' Force redraw with latest hash rate
                End If
            End While
        Catch exThread As ThreadInterruptedException
            UpdateConsoleDisplay("Hash rate display thread interrupted.", ConsoleColor.Yellow, False, True, True)
        Catch ex As Exception
            If Not _stopClient Then UpdateConsoleDisplay($"Error in hash rate display: {ex.ToString()}", ConsoleColor.DarkRed, False, True, True)
        Finally
            UpdateConsoleDisplay("Hash rate display stopped.", ConsoleColor.Gray, False, True, True)
        End Try
    End Sub

    Private Function FormatHashRate(hashRate As Double) As String
        If hashRate < 0 Then hashRate = 0
        If hashRate < 1000 Then
            Return $"{hashRate:F2} H/s"
        ElseIf hashRate < 1000000 Then
            Return $"{hashRate / 1000:F2} kH/s"
        ElseIf hashRate < 1000000000 Then
            Return $"{hashRate / 1000000:F2} MH/s"
        Else
            Return $"{hashRate / 1000000000:F2} GH/s"
        End If
    End Function
    Private Sub UpdateConsoleDisplay(message As String, color As ConsoleColor, Optional important As Boolean = False, Optional noClear As Boolean = False, Optional isHashRateUpdateOnly As Boolean = False)
        SyncLock _consoleLock
            ' --- EXTREMELY SIMPLIFIED FOR ARM64 DEBUGGING / STABILITY ---
            Dim prefix As String = ""
            If Not isHashRateUpdateOnly AndAlso Not String.IsNullOrEmpty(message) Then
                prefix = $"[{DateTime.UtcNow:HH:mm:ss.fff}] CLNT_LOG [{color}]: "
                Console.WriteLine(prefix & message)
            End If

            ' Always print hash rate info if it's a hash rate update or if no other message was provided
            If isHashRateUpdateOnly OrElse String.IsNullOrEmpty(message) Then
                Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] CLNT_HR: Target: {_currentMiningTargetInfo} | Diff: {_currentJobDifficulty} | Rate: {FormatHashRate(_currentHashRate)} | Total: {Interlocked.Read(_hashCount)}")
            End If
            ' --- END SIMPLIFIED ---
        End SyncLock
    End Sub

    Public Sub Kill()
        _stopClient = True ' Signal all loops to stop
        _currentMiningJobCts?.Cancel() ' Cancel any active mining job or wait on Take
        UpdateConsoleDisplay("Disconnecting...", ConsoleColor.Yellow, True)

        Try
            _workPackageQueue?.CompleteAdding() ' Signal no more items will be added, allows Take to exit
        Catch
            ' Ignore exceptions if already completed or disposed
        End Try

        ' Attempt to interrupt threads if they are sleeping or waiting
        _primaryReaderThread?.Interrupt()
        _miningWorkerThread?.Interrupt() ' Though CancellationToken is preferred
        _hashRateDisplayThread?.Interrupt()

        ' Join threads to wait for them to finish
        If _primaryReaderThread IsNot Nothing AndAlso _primaryReaderThread.IsAlive Then
            _primaryReaderThread.Join(TimeSpan.FromSeconds(2))
        End If
        If _miningWorkerThread IsNot Nothing AndAlso _miningWorkerThread.IsAlive Then
            _miningWorkerThread.Join(TimeSpan.FromSeconds(3)) ' Give mining a bit longer to unwind
        End If
        If _hashRateDisplayThread IsNot Nothing AndAlso _hashRateDisplayThread.IsAlive Then
            _hashRateDisplayThread.Join(TimeSpan.FromSeconds(1))
        End If

        ' Clean up resources
        Try
            _currentMiningJobCts?.Dispose()
        Catch
        End Try
        Try
            _serverStreamWriter?.Close() ' Close first to flush
            _serverStreamWriter?.Dispose()
        Catch
        End Try
        Try
            _client?.Close() ' This closes the underlying stream too
            _client?.Dispose()
        Catch ex As Exception
            UpdateConsoleDisplay($"Error closing client resources: {ex.Message}", ConsoleColor.DarkYellow, False, True)
        End Try
        UpdateConsoleDisplay("Disconnected from server.", ConsoleColor.Gray, False, True)
    End Sub

End Class