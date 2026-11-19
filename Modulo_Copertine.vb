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
    ' filtroNomeFile è mantenuto per compatibilità con le chiamate precedenti.
    ' massimoSchede=3 limita il primo test; impostare 0 per tutte le schede.
    Public Async Function ScaricaCollanaAsync(urlIndice As String,
                                               filtroNomeFile As String,
                                               cartellaDestinazione As String,
                                               Optional massimoSchede As Integer = 3) As Task(Of List(Of EsitoCopertina))

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

                    ' Il collegamento alla copertina principale è uno script PHP.
                    ' Si usa l'URL effettivo della pagina, senza dedurre il nome JPG.
                    ' Cerca i link Fancybox e identifica la copertina tramite
                    ' il parametro idimg=0, senza dipendere dall'ordine dei parametri.
                    Dim linkImmagini As Integer = 0
                    For Each link As Match In Regex.Matches(html,
                        "<a\\b[^>]*href\\s*=\\s*[""'](?<url>[^""']+)[""']",
                        RegexOptions.IgnoreCase Or RegexOptions.Singleline)

                        Dim indirizzo As String = WebUtility.HtmlDecode(link.Groups("url").Value)
                        If Not indirizzo.Contains("showimg.php", StringComparison.OrdinalIgnoreCase) Then Continue For
                        linkImmagini += 1

                        Dim immagine As Uri = Nothing
                        If Not Uri.TryCreate(pagina, indirizzo, immagine) Then Continue For
                        If Not immagine.Host.Equals(pagina.Host, StringComparison.OrdinalIgnoreCase) Then Continue For

                        Dim parametri As String = immagine.Query.TrimStart("?"c)
                        If Not Regex.IsMatch(parametri, "(?:^|&)idimg=0(?:&|$)", RegexOptions.IgnoreCase) Then Continue For

                        candidati.Add(immagine)
                        Exit For
                    Next

                    If candidati.Count = 0 Then
                        esito.Messaggio = "Collegamenti showimg.php trovati: " & linkImmagini &
                                         "; HTML: " & html.Length & " caratteri"
                    End If

                    For Each immagine As Uri In candidati
                        Try
                            Using risposta As HttpResponseMessage = Await client.GetAsync(immagine)
                                If Not risposta.IsSuccessStatusCode Then Continue For
                                Dim dati As Byte() = Await risposta.Content.ReadAsByteArrayAsync()
                                If dati.Length < 10000 Then
                                    esito.Messaggio = "Risposta troppo piccola (" & dati.Length & " byte)"
                                    Continue For
                                End If

                                Using memoria As New MemoryStream(dati)
                                    Using bitmap As Image = Image.FromStream(memoria)
                                        If bitmap.Width < 400 OrElse bitmap.Height < 500 Then
                                            esito.Messaggio = "Immagine troppo piccola: " & bitmap.Width & "x" & bitmap.Height
                                            Continue For
                                        End If
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
                            esito.Messaggio = "Errore immagine: " & ex.Message
                        End Try
                    Next

                    If esito.FileSalvato = "" AndAlso esito.Messaggio = "" Then esito.Messaggio = "Nessuna immagine salvata"
                Catch ex As Exception
                    esito.Messaggio = "Errore scheda: " & ex.Message
                End Try

                ' Una richiesta alla volta, per non sovraccaricare il sito.
                If massimoSchede > 0 AndAlso risultati.Count >= massimoSchede Then Exit For
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
