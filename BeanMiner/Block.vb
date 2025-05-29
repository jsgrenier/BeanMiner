Imports System.Security.Cryptography
Imports System.Text
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Globalization
' Imports Newtonsoft.Json.Converters ' Not strictly needed if not using IsoDateTimeConverter directly in CalculateHash

Public Class Block
    Public Property Index As Integer
    Public Property Timestamp As DateTime ' Stored as DateTime
    Public Property Data As List(Of JObject)
    Public Property PreviousHash As String
    Public Property Hash As String
    Public Property Nonce As Integer
    Public Property Difficulty As Integer
    Public Property BlockSize As Integer

    ' Keep this for debugging outside this class if needed, but CalculateHash won't write to it now.
    Public Shared LastCalculatedDataToHash As String = ""

    Public Const PreciseTimestampFormat As String = "yyyy-MM-ddTHH:mm:ss.fffffffZ"

    Public Sub New(index As Integer, timestamp As DateTime, data As List(Of JObject), previousHash As String, difficulty As Integer)
        Me.Index = index
        Me.Timestamp = timestamp
        Me.Data = data
        Me.PreviousHash = previousHash
        Me.Difficulty = difficulty
        Me.Nonce = 0
        Me.Hash = CalculateHash()
        Me.BlockSize = CalculateBlockSize()
    End Sub

    <JsonConstructor>
    Public Sub New(index As Integer, timestamp As DateTime, data As List(Of JObject), previousHash As String, hash As String, nonce As Integer, difficulty As Integer, blockSize As Integer)
        Me.Index = index
        Me.Timestamp = timestamp
        Me.Data = data
        Me.PreviousHash = previousHash
        Me.Hash = hash
        Me.Nonce = nonce
        Me.Difficulty = difficulty
        Me.BlockSize = blockSize
    End Sub

    Public Function CalculateHash() As String
        Dim dataJson As String
        Try
            ' --- Manual construction of dataJson ---
            If Me.Data Is Nothing OrElse Me.Data.Count = 0 Then
                dataJson = "[]"
            Else
                Dim sbJsonArray As New StringBuilder("[")
                For i As Integer = 0 To Me.Data.Count - 1
                    Dim jobjWrapper As JObject = Me.Data(i)
                    If jobjWrapper IsNot Nothing Then
                        ' JObject.ToString(Formatting.None) should be literal for its JValue(String) properties
                        ' assuming the JValue was created as a string and not re-parsed as a date internally.
                        sbJsonArray.Append(jobjWrapper.ToString(Formatting.None))
                    Else
                        sbJsonArray.Append("null") ' Should not happen if Data is clean
                    End If
                    If i < Me.Data.Count - 1 Then
                        sbJsonArray.Append(",")
                    End If
                Next
                sbJsonArray.Append("]")
                dataJson = sbJsonArray.ToString()
            End If
            ' --- End Manual construction ---

        Catch ex As Exception
            ' It's good to log critical errors, but for release, you might have a more robust logging system.
            ' Console.WriteLine($"CRITICAL ERROR during Me.Data serialization in CalculateHash: {ex.ToString()}")
            dataJson = "[]" ' Fallback to prevent crash, this will cause hash mismatch if error occurs
        End Try

        Dim formattedBlockTimestamp As String = Me.Timestamp.ToUniversalTime().ToString(PreciseTimestampFormat, CultureInfo.InvariantCulture)

        Dim tempDataToHash As String = Me.Index.ToString() &
                                     formattedBlockTimestamp &
                                     dataJson &
                                     Me.PreviousHash &
                                     Me.Nonce.ToString() &
                                     Me.Difficulty.ToString()

        ' LastCalculatedDataToHash = tempDataToHash ' You can uncomment this if you need to debug again later.

        Return HashString(tempDataToHash)
    End Function

    Public Sub Mine()
        Dim leadingZeros As String = New String("0"c, Me.Difficulty)
        Me.Nonce = 0
        Me.Hash = CalculateHash()
        While Not Me.Hash.StartsWith(leadingZeros)
            Me.Nonce += 1
            Me.Hash = CalculateHash()
            If Me.Nonce = Integer.MaxValue Then
                ' This is an extreme edge case, means difficulty is likely too high for current processing power
                ' or there's an issue in the mining loop / hash calculation making it impossible to find a solution.
                ' For a real system, you might log this or have other behavior.
                Exit While
            End If
        End While
        Me.BlockSize = CalculateBlockSize()
    End Sub

    Private Function HashString(input As String) As String
        Using sha256 As SHA256 = SHA256.Create()
            Dim bytes As Byte() = Encoding.UTF8.GetBytes(input)
            Dim hashBytes As Byte() = sha256.ComputeHash(bytes)
            Return BitConverter.ToString(hashBytes).Replace("-", "").ToLower()
        End Using
    End Function

    Public Function CalculateBlockSize() As Integer
        ' Create a temporary JObject to represent the block for serialization to get size.
        ' This ensures we are measuring the size of the fields as they would be serialized.
        Dim tempBlockForSizing As New JObject()
        tempBlockForSizing("Index") = Me.Index
        tempBlockForSizing("Timestamp") = Me.Timestamp.ToUniversalTime().ToString(PreciseTimestampFormat, CultureInfo.InvariantCulture)
        tempBlockForSizing("Data") = JToken.FromObject(Me.Data) ' Represents the list of JObjects
        tempBlockForSizing("PreviousHash") = Me.PreviousHash
        tempBlockForSizing("Hash") = Me.Hash ' Include current hash
        tempBlockForSizing("Nonce") = Me.Nonce
        tempBlockForSizing("Difficulty") = Me.Difficulty
        ' BlockSize itself is not part of the string used to calculate BlockSize to avoid recursion.

        Dim blockDataString = tempBlockForSizing.ToString(Formatting.None)
        Return Encoding.UTF8.GetBytes(blockDataString).Length
    End Function
End Class