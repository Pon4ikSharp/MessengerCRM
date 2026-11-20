using ClosedXML.Excel;

namespace MessengerCRM.Server;

public sealed class ExcelImportService
{
    private readonly DatabaseService _db;
    public ExcelImportService(DatabaseService db) => _db = db;

    public ImportResult Import(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheets.FirstOrDefault()
            ?? throw new InvalidOperationException("В Excel немає аркушів.");
        var used = ws.RangeUsed();
        if (used is null) return new ImportResult(0, 0, 0);

        var firstRow = used.RangeAddress.FirstAddress.RowNumber;
        var lastRow = used.RangeAddress.LastAddress.RowNumber;
        var firstCol = used.RangeAddress.FirstAddress.ColumnNumber;
        var lastCol = used.RangeAddress.LastAddress.ColumnNumber;

        int headerRow = -1;
        int? nameCol = null;
        int? phoneCol = null;
        int? notesCol = null;

        // 1) Спочатку шукаємо звичайні заголовки у перших 20 рядках.
        var scanTo = Math.Min(lastRow, firstRow + 19);
        for (var r = firstRow; r <= scanTo; r++)
        {
            var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var c = firstCol; c <= lastCol; c++)
            {
                var h = NormalizeHeader(ws.Cell(r, c).GetFormattedString());
                if (h.Length > 0) headers[h] = c;
            }

            nameCol = Find(headers,
                "ім'я", "ім’я", "імя", "имя", "name", "піб", "фіо",
                "піб / назва", "піб/назва", "контакт", "контактна особа", "клієнт", "клиент");
            phoneCol = Find(headers,
                "телефон", "номер", "номер телефону", "телефонний номер", "мобільний",
                "мобільний телефон", "моб. телефон", "phone", "phone number", "mobile", "моб", "тел");
            notesCol = Find(headers,
                "примітка", "примечание", "notes", "коментар", "комментарий", "comment");

            if (nameCol is not null && phoneCol is not null)
            {
                headerRow = r;
                break;
            }
        }

        // 2) Якщо заголовків немає — визначаємо колонки за самими даними.
        if (nameCol is null || phoneCol is null)
        {
            var detected = DetectColumns(ws, firstRow, lastRow, firstCol, lastCol);
            nameCol ??= detected.nameCol;
            phoneCol ??= detected.phoneCol;
            notesCol ??= detected.notesCol;
        }

        if (nameCol is null || phoneCol is null)
            throw new InvalidOperationException("Не зміг автоматично визначити колонки з ім'ям і телефоном. У файлі мають бути окремі колонки з ПІБ/ім'ям та номером телефону.");

        var dataStartRow = headerRow >= 0 ? headerRow + 1 : firstRow;
        var added = 0;
        var duplicates = 0;
        var invalid = 0;

        for (var r = dataStartRow; r <= lastRow; r++)
        {
            var name = ws.Cell(r, nameCol.Value).GetFormattedString().Trim();
            var phone = ws.Cell(r, phoneCol.Value).GetFormattedString().Trim();
            var notes = notesCol is null ? "" : ws.Cell(r, notesCol.Value).GetFormattedString().Trim();

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(phone)) continue;
            if (_db.TryAddImportedContact(name, phone, notes, out var duplicate)) added++;
            else if (duplicate) duplicates++;
            else invalid++;
        }

        return new ImportResult(added, duplicates, invalid);
    }

    private static (int? nameCol, int? phoneCol, int? notesCol) DetectColumns(
        IXLWorksheet ws, int firstRow, int lastRow, int firstCol, int lastCol)
    {
        var sampleTo = Math.Min(lastRow, firstRow + 79);
        int? bestPhoneCol = null;
        var bestPhoneScore = 0.0;
        int? bestNameCol = null;
        var bestNameScore = 0.0;

        for (var c = firstCol; c <= lastCol; c++)
        {
            var nonEmpty = 0;
            var phoneHits = 0;
            var textNameHits = 0;

            for (var r = firstRow; r <= sampleTo; r++)
            {
                var value = ws.Cell(r, c).GetFormattedString().Trim();
                if (value.Length == 0) continue;
                nonEmpty++;

                var normalized = DatabaseService.NormalizePhone(value);
                if (DatabaseService.IsPlausiblePhone(normalized))
                    phoneHits++;

                if (LooksLikeName(value))
                    textNameHits++;
            }

            if (nonEmpty == 0) continue;
            var phoneScore = (double)phoneHits / nonEmpty;
            var nameScore = (double)textNameHits / nonEmpty;

            if (phoneHits >= 1 && phoneScore > bestPhoneScore)
            {
                bestPhoneScore = phoneScore;
                bestPhoneCol = c;
            }
            if (textNameHits >= 1 && nameScore > bestNameScore)
            {
                bestNameScore = nameScore;
                bestNameCol = c;
            }
        }

        // Не дозволяємо одній колонці бути і телефоном, і ім'ям.
        if (bestNameCol == bestPhoneCol)
        {
            bestNameCol = null;
            bestNameScore = 0;
            for (var c = firstCol; c <= lastCol; c++)
            {
                if (c == bestPhoneCol) continue;
                var nonEmpty = 0;
                var nameHits = 0;
                for (var r = firstRow; r <= sampleTo; r++)
                {
                    var value = ws.Cell(r, c).GetFormattedString().Trim();
                    if (value.Length == 0) continue;
                    nonEmpty++;
                    if (LooksLikeName(value)) nameHits++;
                }
                if (nonEmpty == 0) continue;
                var score = (double)nameHits / nonEmpty;
                if (nameHits >= 1 && score > bestNameScore)
                {
                    bestNameScore = score;
                    bestNameCol = c;
                }
            }
        }

        // Вимагаємо хоча б приблизно половину телефоноподібних значень у вибраній колонці.
        if (bestPhoneScore < 0.40) bestPhoneCol = null;
        if (bestNameScore < 0.40) bestNameCol = null;

        return (bestNameCol, bestPhoneCol, null);
    }

    private static bool LooksLikeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 150) return false;
        if (DatabaseService.IsPlausiblePhone(DatabaseService.NormalizePhone(value))) return false;
        var letters = value.Count(char.IsLetter);
        return letters >= 2;
    }

    private static string NormalizeHeader(string value)
    {
        return (value ?? "")
            .Replace('\u00A0', ' ')
            .Trim()
            .ToLowerInvariant();
    }

    private static int? Find(Dictionary<string, int> headers, params string[] names)
    {
        foreach (var n in names)
            if (headers.TryGetValue(NormalizeHeader(n), out var c)) return c;
        return null;
    }
}
