Imports System.Security.Cryptography
Imports System.Text
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Globalization

Public Class Block
    Public Property Index As Integer
    Public Property Timestamp As DateTime ' Should be DateTime
    Public Property Data As List(Of JObject)
    Public Property PreviousHash As String
    Public Property Hash As String
    Public Property Nonce As Integer
    Public Property Difficulty As Integer
    Public Property BlockSize As Integer

    Public Shared LastCalculatedDataToHash As String = "" ' For debugging

    ' Constructor for new blocks (client mining, server genesis)
    Public Sub New(index As Integer, timestamp As DateTime, data As List(Of JObject), previousHash As String, difficulty As Integer) ' <-- ADD difficulty
        Me.Index = index
        Me.Timestamp = timestamp
        Me.Data = data
        Me.PreviousHash = previousHash
        Me.Difficulty = difficulty ' <-- SET THIS
        Me.Nonce = 0
        Me.Hash = CalculateHash() ' CalculateHash doesn't use Me.Difficulty
        Me.BlockSize = CalculateBlockSize()
    End Sub

    ' Constructor for deserialization by Newtonsoft.Json
    <JsonConstructor>
    Public Sub New(index As Integer, timestamp As DateTime, data As List(Of JObject), previousHash As String, hash As String, nonce As Integer, difficulty As Integer, blockSize As Integer) ' <-- ADD difficulty
        Me.Index = index
        Me.Timestamp = timestamp
        Me.Data = data
        Me.PreviousHash = previousHash
        Me.Hash = hash
        Me.Nonce = nonce
        Me.Difficulty = difficulty ' <-- SET THIS
        Me.BlockSize = blockSize
    End Sub

    Public Function CalculateHash() As String
        Dim dataJson As String = JsonConvert.SerializeObject(Me.Data, Formatting.None,
                                                       New JsonSerializerSettings With {
                                                           .Culture = CultureInfo.InvariantCulture
                                                       })
        Dim tempDataToHash As String = Me.Index.ToString() & Me.Timestamp.ToString("o", CultureInfo.InvariantCulture) & dataJson & Me.PreviousHash & Me.Nonce.ToString()

        LastCalculatedDataToHash = tempDataToHash ' Store it for inspection

        Return HashString(tempDataToHash)
    End Function

    ' Simple proof of work to "mine"
    Public Sub Mine() ' Renamed from Mine(difficulty) - it will use its own Difficulty property
        Dim leadingZeros As String = New String("0"c, Me.Difficulty) ' Use Me.Difficulty
        Me.Hash = CalculateHash()
        While Not Me.Hash.StartsWith(leadingZeros)
            Me.Nonce += 1
            Me.Hash = CalculateHash()
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
        Dim blockDataString = JsonConvert.SerializeObject(Me, Formatting.None)
        Return Encoding.UTF8.GetBytes(blockDataString).Length
    End Function
End Class