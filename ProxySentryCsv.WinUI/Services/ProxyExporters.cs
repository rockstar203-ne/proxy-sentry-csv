using System.IO.Compression;
using System.Security;
using System.Text;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public static class ProxyExporters
{
    public static async Task ExportPlainTxtAsync(string path, IReadOnlyList<ProxyEntry> records)
    {
        await File.WriteAllLinesAsync(path, records.Select(record => record.StandardProxy), Encoding.UTF8);
    }

    public static async Task ExportProxyCsvAsync(string path, IReadOnlyList<ProxyEntry> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Proxy,Name,Email,Browser,Country,Region,City");
        foreach (var record in records)
        {
            builder.Append(Csv(record.StandardProxy));
            builder.Append(',');
            builder.Append(Csv(record.DisplayName));
            builder.Append(',');
            builder.Append(Csv(record.Email));
            builder.Append(',');
            builder.Append(Csv(record.BrowserProvider));
            builder.Append(',');
            builder.Append(Csv(record.CountryCode));
            builder.Append(',');
            builder.Append(Csv(record.Region));
            builder.Append(',');
            builder.Append(Csv(record.City));
            builder.AppendLine();
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8);
    }

    public static async Task ExportAdsTxtAsync(string path, IReadOnlyList<ProxyEntry> records, string profileBaseName)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var name = ProfileName(profileBaseName, i + 1);
            builder.AppendLine($"acc_id={name}");
            builder.AppendLine("id=");
            builder.AppendLine("group=");
            builder.AppendLine($"name={name}");
            builder.AppendLine("remark=");
            builder.AppendLine("platform=");
            builder.AppendLine("username=");
            builder.AppendLine("password=");
            builder.AppendLine("fakey=");
            builder.AppendLine("cookie=");
            builder.AppendLine($"proxytype={record.Scheme.ToLowerInvariant()}");
            builder.AppendLine("ipchecker=ip2location");
            builder.AppendLine($"proxy={record.StandardProxy}");
            builder.AppendLine("proxyurl=");
            builder.AppendLine("proxyid=");
            builder.AppendLine($"ip={record.Host}");
            builder.AppendLine("countrycode=");
            builder.AppendLine("ua=");
            builder.AppendLine("******************");
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8);
    }

    public static async Task ExportAdsXlsxAsync(string path, IReadOnlyList<ProxyEntry> records, string profileBaseName)
    {
        await Task.Run(() =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            AddEntry(archive, "[Content_Types].xml", ContentTypesXml());
            AddEntry(archive, "_rels/.rels", RootRelsXml());
            AddEntry(archive, "xl/workbook.xml", WorkbookXml());
            AddEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelsXml());
            AddEntry(archive, "xl/styles.xml", StylesXml());
            AddEntry(archive, "xl/worksheets/sheet1.xml", SheetXml(records, profileBaseName));
        });
    }

    public static string ProfileName(string baseName, int index)
    {
        var clean = string.IsNullOrWhiteSpace(baseName) ? "profile" : baseName.Trim();
        return $"{clean}{index}";
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string ContentTypesXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private static string RootRelsXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string WorkbookXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private static string WorkbookRelsXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    private static string StylesXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
        "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";

    private static string SheetXml(IReadOnlyList<ProxyEntry> records, string profileBaseName)
    {
        var headers = new[]
        {
            "name", "remark", "tab", "platform", "username", "password", "fakey", "cookie", "proxytype",
            "ipchecker", "proxy", "proxyurl", "proxyid", "ip", "countrycode", "regioncode", "citycode", "ua", "resolution"
        };
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        builder.Append($"<dimension ref=\"A1:S{records.Count + 1}\"/><sheetViews><sheetView workbookViewId=\"0\"/></sheetViews><sheetFormatPr defaultRowHeight=\"15\"/><sheetData>");
        builder.Append("<row r=\"1\">");
        for (var i = 0; i < headers.Length; i++)
        {
            builder.Append(Cell(i + 1, 1, headers[i], 1));
        }
        builder.Append("</row>");

        for (var i = 0; i < records.Count; i++)
        {
            var rowIndex = i + 2;
            var record = records[i];
            var values = new[]
            {
                ProfileName(profileBaseName, i + 1), "", "", "", "", "", "", "", record.Scheme.ToLowerInvariant(),
                "ip2location", record.StandardProxy, "", "", record.Host, "", "", "", "", ""
            };
            builder.Append($"<row r=\"{rowIndex}\">");
            for (var col = 0; col < values.Length; col++)
            {
                if (!string.IsNullOrEmpty(values[col]))
                {
                    builder.Append(Cell(col + 1, rowIndex, values[col], 0));
                }
            }
            builder.Append("</row>");
        }

        builder.Append("</sheetData></worksheet>");
        return builder.ToString();
    }

    private static string Cell(int columnIndex, int rowIndex, string value, int style)
    {
        return $"<c r=\"{ColumnName(columnIndex)}{rowIndex}\" s=\"{style}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(value)}</t></is></c>";
    }

    private static string ColumnName(int index)
    {
        var name = "";
        while (index > 0)
        {
            index--;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }
        return name;
    }

    private static string Csv(string value)
    {
        value ??= "";
        return value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
