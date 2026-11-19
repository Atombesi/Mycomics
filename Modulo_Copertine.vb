Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Drawing

' Modulo sperimentale indipendente dal database MyComics.
' Fonte iniziale: collezionismofumetti.com. Nessuna modifica a ALBI/MYCOPIE.
Public Module Modulo_Copertine

    Public Class EsitoCopertina
        Public Property Numero As String = ""
        Public Property Pagina As String = ""
        Public Property FileSalvato As String = ""
        Public Property Messaggio As String = ""
    End Class

    ' Esempio:
    ' Dim risultati = Await Modulo_Copertine.ScaricaCollanaAsync(
    '     "https://www.collezionismofumetti.com/fumetti.php?idx=51173",
    '     "Arthur_King", "C:\Copertine\ArthurKing")
    '
    ' Eseguire da un evento Async (non blocca la UI).
    ' Il parametro filtroNomeFile deve essere una parte del nome delle immagini
    ' (es. Arthur_King), non il titolo visualizzato nella pagina.
    Public Async Function ScaricaCollanaAsync(urlIndice As String,
                                               filtroNomeFile As String,
                                               cartellaDestinazione As String) As Task(Of List(Of EsitoCopertina))

        Dim risultati As New List(Of EsitoCopertina)
        Directory.CreateDirectory(cartellaDestinazione)

        Using client As New HttpClient()
            client.Timeout = TimeSpan.FromSeconds(25)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MyComics-CoverImporter/0.1 (personal catalog)")
            Dim htmlIndice As String = Await client.GetStringAsync(urlIndice)
            Dim baseUri As New Uri(urlIndice)

            ' Legge i collegamenti alle schede degli albi, senza inventare
            ' sequenze numeriche o URL.
            Dim visti As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim patternLink As String = "<a\b[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<testo>.*?)</a>"
            For Each m As Match In Regex.Matches(htmlIndice, patternLink,
                                                 RegexOptions.IgnoreCase Or RegexOptions.Singleline)
                Dim href As String = WebUtility.HtmlDecode(m.Groups("href").Value)
                If Not href.Contains("fumetti.php?idx=", StringComparison.OrdinalIgnoreCase) Then Continue For

                Dim testo As String = Regex.Replace(m.Groups("testo").Value, "<[^>]*>", "").Trim()
                testo = WebUtility.HtmlDecode(testo)
                Dim numMatch As Match = Regex.Match(testo, "^\s*(\d+(?:\s*/\s*\d+)?)\s*(?:-|$)")
                If Not numMatch.Success Then Continue For

                Dim numero As String = Regex.Replace(numMatch.Groups(1).Value, "\s+", "")
                Dim pagina As New Uri(baseUri, href)
                If Not visti.Add(pagina.AbsoluteUri) Then Continue For

                Dim esito As New EsitoCopertina With {.Numero = numero, .Pagina = pagina.AbsoluteUri}
                risultati.Add(esito)
                Try
                    Dim html As String = Await client.GetStringAsync(pagina)
                    Dim candidati As New List(Of Uri)

                    ' Esamina i collegamenti e le immagini presenti nella scheda.
                    ' Il sito può usare miniature TH_ e file originali distinti.
                    For Each mm As Match In Regex.Matches(html,
                        "(?:href|src)\s*=\s*[""'](?<url>[^""']+\.(?:jpg|jpeg|png)(?:\?[^""']*)?)[""']",
                        RegexOptions.IgnoreCase)
                        Dim indirizzo As String = WebUtility.HtmlDecode(mm.Groups("url").Value)
                        Dim immagine As Uri = Nothing
                        If Not Uri.TryCreate(pagina, indirizzo, immagine) Then Continue For
                        If immagine.Host <> pagina.Host Then Continue For
                        Dim nome As String = Path.GetFileName(immagine.AbsolutePath)
                        If Not nome.Contains(filtroNomeFile, StringComparison.OrdinalIgnoreCase) Then Continue For
                        If Not candidati.Any(Function(u) u.AbsoluteUri = immagine.AbsoluteUri) Then candidati.Add(immagine)

                        ' Ipotesi verificata solo quando il download restituisce
                        ' effettivamente un'immagine grande.
                        If nome.StartsWith("TH_", StringComparison.OrdinalIgnoreCase) Then
                            Dim originale As New Uri(immagine, nome.Substring(3))
                            If Not candidati.Any(Function(u) u.AbsoluteUri = originale.AbsoluteUri) Then candidati.Add(originale)
                        End If
                    Next

                    ' Prima immagini senza TH_; il controllo delle dimensioni
                    ' esclude comunque le miniature.
                    candidati = candidati.OrderBy(Function(u) If(Path.GetFileName(u.AbsolutePath).StartsWith("TH_", StringComparison.OrdinalIgnoreCase), 1, 0)).ToList()

                    For Each immagine As Uri In candidati
                        Try
                            Using risposta As HttpResponseMessage = Await client.GetAsync(immagine)
                                If Not risposta.IsSuccessStatusCode Then Continue For
                                Dim dati As Byte() = Await risposta.Content.ReadAsByteArrayAsync()
                                If dati.Length < 10000 Then Continue For

                                Using memoria As New MemoryStream(dati)
                                    Using bitmap As Image = Image.FromStream(memoria)
                                        If bitmap.Width < 400 OrElse bitmap.Height < 500 Then Continue For
                                        Dim estensione As String = If(bitmap.RawFormat.Guid = Imaging.ImageFormat.Png.Guid, ".png", ".jpg")
                                        Dim nomeFile As String = "Copertina_" & numero.Replace("/", "-") & estensione
                                        Dim destinazione As String = Path.Combine(cartellaDestinazione, nomeFile)

                                        If File.Exists(destinazione) Then
                                            esito.Messaggio = "Già presente: non sovrascritto"
                                        Else
                                            File.WriteAllBytes(destinazione, dati)
                                            esito.Messaggio = "Scaricata"
                                        End If
                                        esito.FileSalvato = destinazione
                                        Exit For
                                    End Using
                                End Using
                            End Using
                        Catch ex As Exception
                            ' Si prova il candidato successivo.
                        End Try
                    Next

                    If esito.FileSalvato = "" Then esito.Messaggio = "Nessuna copertina grande individuata"
                Catch ex As Exception
                    esito.Messaggio = "Errore scheda: " & ex.Message
                End Try

                ' Una richiesta alla volta, per non sovraccaricare il sito.
                Await Task.Delay(350)
            Next
        End Using

        Dim righe As New List(Of String) From {"Numero;File;Esito;Pagina"}
        For Each e In risultati
            righe.Add(String.Join(";", Csv(e.Numero), Csv(e.FileSalvato), Csv(e.Messaggio), Csv(e.Pagina)))
        Next
        File.WriteAllLines(Path.Combine(cartellaDestinazione, "esito_copertine.csv"), righe, Encoding.UTF8)
        Return risultati
    End Function

    Private Function Csv(testo As String) As String
        Return """" & If(testo, "").Replace("""", """""") & """"
    End Function

End Module
