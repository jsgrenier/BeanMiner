Imports System.IO
Imports System.Net.Sockets
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Public Class MiningClient

    Private _serverAddress As String
    Private _serverPort As Integer
    Private _client As TcpClient
    Private _difficulty As Integer ' Store the mining difficulty locally
    Private _minerAddress As String


    ' Track the last displayed status to update the console dynamically
    Private _lastStatus As String = String.Empty


    Private _lastHashRateUpdate As DateTime = DateTime.Now
    Private _hashRateSum As Double = 0
    Private _hashRateCount As Integer = 0

    Private _hashCount As Decimal = 0 ' Use Decimal for higher precision
    Private _hashRate As Double = 0
    Private _stopHashRateThread As Boolean = False

    Public Sub New(serverAddress As String, serverPort As Integer, minerAddress As String)
        _serverAddress = serverAddress
        _serverPort = serverPort
        _minerAddress = minerAddress

        _lastHashRateUpdate = DateTime.Now
        _hashRateSum = 0
        _hashRateCount = 0
    End Sub

    Public Sub Start()
        Try
            _client = New TcpClient(_serverAddress, _serverPort)
            UpdateConsoleStatus($"[INFO] Connected to mining server: {_serverAddress}:{_serverPort}")

            Dim clientThread As New Thread(AddressOf Mine)
            clientThread.Start()

            ' Start the hash rate calculation thread
            Dim hashRateThread As New Thread(AddressOf CalculateHashRate)
            hashRateThread.Start()

        Catch ex As Exception
            UpdateConsoleStatus($"[ERROR] Error connecting to server: {ex.Message}")
        End Try
    End Sub

    Public Sub Kill()
        If _client IsNot Nothing Then
            _client.Close()
        End If
        _stopHashRateThread = True ' Signal the hash rate thread to stop
        UpdateConsoleStatus("[INFO] Disconnected from mining server.")
    End Sub

    Private Sub Mine()
        Try
            Dim stream As NetworkStream = _client.GetStream()
            Dim reader As New StreamReader(stream)
            Dim writer As New StreamWriter(stream)
            writer.AutoFlush = True

            While True
                ' --- Receive blockchain information ---
                Dim blockchainInfoData As String = Nothing

                While String.IsNullOrEmpty(blockchainInfoData)
                    blockchainInfoData = reader.ReadLine()
                    ' Add a small delay to prevent tight looping
                    Thread.Sleep(100)
                End While

                ' --- Check if blockchainInfoData is valid before parsing ---
                If Not String.IsNullOrEmpty(blockchainInfoData) Then
                    Try
                        Dim blockchainInfo = JObject.Parse(blockchainInfoData)

                        Dim lastIndex As Integer = CInt(blockchainInfo("lastIndex"))
                        Dim lastHash As String = blockchainInfo("lastHash").ToString()

                        _difficulty = blockchainInfo("difficulty")

                        UpdateConsoleStatus($"[INFO] Received blockchain info. Last Index: {lastIndex}, Last Hash: {lastHash}, difficulty: {_difficulty}")

                        ' --- Receive mempool transactions ---
                        Dim mempoolData As String = reader.ReadLine()

                        If String.IsNullOrEmpty(mempoolData) Then
                            UpdateConsoleStatus("[INFO] No transactions in the mempool. Waiting...")
                            Continue While
                        End If

                        Dim mempoolTransactions = JsonConvert.DeserializeObject(Of List(Of JObject))(mempoolData)
                        UpdateConsoleStatus($"[INFO] Received {mempoolTransactions.Count} transactions from the mempool.")

                        If mempoolTransactions.Count > 0 Then
                            Dim newIndex As Integer = lastIndex + 1

                            UpdateConsoleStatus("[INFO] Mining new block...")

                            _hashCount = 0 ' Reset hash count

                            Dim blockFound As Boolean = False ' Flag to indicate if a block has been found

                            ' --- Parallel Mining ---
                            Parallel.For(0, Environment.ProcessorCount, Sub(coreIndex)
                                                                            Dim localNonce As Integer = coreIndex
                                                                            Dim localBlock As New Block(newIndex, DateTime.Now, mempoolTransactions, lastHash) ' Create a local block for each thread

                                                                            Do
                                                                                If blockFound Then Exit Do ' Exit the loop if a block has been found by another thread

                                                                                _hashCount += 1 ' Increment the global hash count
                                                                                localBlock.Nonce = localNonce ' Set the starting nonce for this thread
                                                                                localBlock.Hash = localBlock.CalculateHash()
                                                                                localNonce += Environment.ProcessorCount ' Increment the nonce by the number of cores to avoid duplicate work
                                                                            Loop While Not localBlock.Hash.StartsWith(New String("0", _difficulty)) AndAlso Not _stopHashRateThread ' Add _stopHashRateThread check

                                                                            If localBlock.Hash.StartsWith(New String("0", _difficulty)) Then
                                                                                blockFound = True ' Set the flag to indicate that a block has been found

                                                                                UpdateConsoleStatus($"[INFO] Mined new block with hash: {localBlock.Hash} (Thread {coreIndex})")

                                                                                ' --- Send the mined block to the server ---
                                                                                Dim blockData = JsonConvert.SerializeObject(localBlock)
                                                                                ' Create a JObject to hold both the block data and miner address
                                                                                Dim dataToSend = New JObject()
                                                                                dataToSend("block") = JObject.Parse(blockData) ' Embed block data as a JObject
                                                                                dataToSend("minerAddress") = _minerAddress

                                                                                ' Serialize the JObject and send it to the server
                                                                                writer.WriteLine(JsonConvert.SerializeObject(dataToSend) & vbCrLf)
                                                                                UpdateConsoleStatus("[INFO] Sent mined block to the server.")
                                                                            End If
                                                                        End Sub)
                        End If
                    Catch ex As Exception
                        UpdateConsoleStatus($"[ERROR] Error parsing blockchain info: {ex.Message}")
                        UpdateConsoleStatus("[ERROR] Blockchain info data: " & blockchainInfoData)
                    End Try
                End If
            End While

        Catch ex As Exception
            UpdateConsoleStatus($"[ERROR] Error during mining: {ex.Message}")
            Kill()
        End Try
    End Sub

    Private Function CreateNewBlock(transactions As List(Of JObject), lastIndex As Integer, lastHash As String) As Block
        Dim newIndex As Integer = lastIndex + 1
        Return New Block(newIndex, DateTime.Now, transactions, lastHash)
    End Function

    Private Sub CalculateHashRate()
        Dim lastHashCount As Decimal = 0
        Dim lastHashRateUpdate As DateTime = DateTime.Now

        While Not _stopHashRateThread
            Dim currentTime = DateTime.Now
            Dim elapsedSeconds = (currentTime - lastHashRateUpdate).TotalSeconds

            If elapsedSeconds >= 1 Then ' Update hash rate every second
                _hashRate = (_hashCount - lastHashCount) / elapsedSeconds
                lastHashCount = _hashCount
                lastHashRateUpdate = currentTime

                ' Format hash rate based on the chart
                Dim formattedHashRate As String = FormatHashRate(_hashRate)

                ' Update the console with the formatted hash rate (without adding a new line)
                Console.SetCursorPosition(0, Console.CursorTop) ' Move cursor to the beginning of the current line
                Console.Write($"Hash Rate: {formattedHashRate}")
            End If

            Thread.Sleep(100) ' Sleep for a short duration
        End While
    End Sub

    Private Function FormatHashRate(hashRate As Double) As String
        If hashRate < 1000 Then
            Return $"{hashRate:F2} H/s"
        ElseIf hashRate < 1000000 Then
            Return $"{hashRate / 1000:F2} kH/s"
        ElseIf hashRate < 1000000000 Then
            Return $"{hashRate / 1000000:F2} MH/s"
        ElseIf hashRate < 1000000000000 Then
            Return $"{hashRate / 1000000000:F2} GH/s"
        ElseIf hashRate < 1000000000000000 Then
            Return $"{hashRate / 1000000000000:F2} TH/s"
        ElseIf hashRate < 1000000000000000000 Then
            Return $"{hashRate / 1000000000000000:F2} PH/s"
        ElseIf hashRate < 1000000000000000000000D Then
            Return $"{hashRate / 1000000000000000000:F2} EH/s"
        End If
    End Function

    Private Sub UpdateConsoleStatus(message As String)
        If message <> _lastStatus Then
            Console.Clear()
            Console.WriteLine(message)

            ' Calculate average hash rate if enough time has elapsed
            If (DateTime.Now - _lastHashRateUpdate).TotalSeconds >= 1 Then ' Adjust the interval as needed
                Dim averageHashRate = _hashRateSum / _hashRateCount
                _hashRateSum = 0
                _hashRateCount = 0
            End If

            _lastStatus = message
        End If
    End Sub
End Class
