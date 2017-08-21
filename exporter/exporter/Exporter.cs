﻿using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace exporter
{
    public static partial class Exporter
    {
        static Dictionary<string, string> formulaContents = new Dictionary<string, string>();

        static List<string> dataTypes = new List<string>() { "int", "string", "double" };
        static Dictionary<string, DataStruct> datas = new Dictionary<string, DataStruct>();
        class DataStruct
        {
            public readonly string name;
            public bool isnew = true;
            public List<string> files = new List<string>();

            public DataStruct(string name)
            {
                this.name = name;
                datas.Add(name, this);
            }

            public List<string> keys = new List<string>();
            public List<string> keyNames = new List<string>();
            public List<string> types = new List<string>();
            public List<int> cols = new List<int>();
            public Dictionary<string, string[]> groups = new Dictionary<string, string[]>();
            public Dictionary<string, int[]> groupindexs = new Dictionary<string, int[]>();

            public List<int> ids = new List<int>();
            public List<List<object>> dataContent = new List<List<object>>();
        }

        static void Compare(List<string> a, List<string> b, string msg)
        {
            if (a.Count != b.Count) throw new Exception(msg);
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    throw new Exception(msg);
        }
        static void Compare(List<int> a, List<int> b, string msg)
        {
            if (a.Count != b.Count) throw new Exception(msg);
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    throw new Exception(msg);
        }

        static string DealWithDataSheet(ISheet sheet, CustomWorkbook book)
        {
            string tableName = sheet.SheetName;
            if (tableName.StartsWith("_"))
                return string.Empty;

            DataStruct data;
            if (!datas.TryGetValue(tableName, out data))
                lock (datas)
                    data = new DataStruct(tableName);
            lock (data)
            {
                data.files.Add(book.fileName);

                //5、sheet第二行，字段英文名，不填写留空的列将被过滤掉，不予导出，第一列不可留空
                //6、sheet第三行，字段中文名
                //7、sheet第四行，字段类型，int整数、string字符串、double浮点数
                {
                    IRow engRow = sheet.GetRow(1);
                    IRow cnRow = sheet.GetRow(2);
                    IRow tRow = sheet.GetRow(3);

                    if (engRow == null || engRow.FirstCellNum != 0)
                        return "第一个字段不可以留空，SheetName = " + tableName + "，FileName = " + book.fileName;

                    List<string> keys = new List<string>();
                    List<string> keyNames = new List<string>();
                    List<string> types = new List<string>();
                    List<int> cols = new List<int>();
                    for (int i = 0; i < engRow.LastCellNum; i++)
                    {
                        if (engRow.GetCell(i) == null || engRow.GetCell(i).CellType != CellType.String || string.IsNullOrEmpty(engRow.GetCell(i).StringCellValue))
                            continue;
                        cols.Add(i);
                        keys.Add(engRow.GetCell(i).StringCellValue);
                        keyNames.Add((cnRow == null || cnRow.GetCell(i) == null) ? "" : cnRow.GetCell(i).StringCellValue.Replace("\n", " "));
                        string type = (tRow == null || tRow.GetCell(i) == null) ? " " : tRow.GetCell(i).StringCellValue;
                        types.Add(type);
                        if (!dataTypes.Contains(type))
                            return "未知的数据类型" + type + "，SheetName = " + tableName + "，FileName = " + book.fileName;
                    }

                    if (data.isnew)
                    {
                        if (types[0] != "int")
                            return "表头错误，索引必须为int类型，SheetName = " + tableName + "，FileName = " + book.fileName;

                        data.keys = keys;
                        data.keyNames = keyNames;
                        data.types = types;
                        data.cols = cols;
                    }
                    else
                    {
                        string error = "表头不一致，SheetName = " + tableName + "，FileNames = " + string.Join(",", data.files);
                        Compare(keys, data.keys, error);
                        Compare(keyNames, data.keyNames, error);
                        Compare(types, data.types, error);
                        Compare(cols, data.cols, error);
                    }
                }

                // 读取表头
                //4、sheet第一行，填写字段名数据分组，可以进行多字段联合分组("|"分隔)，有分组逻辑的数据必须进行分组
                {
                    List<string> groups = new List<string>();
                    IRow row = sheet.GetRow(0);
                    if (row != null)
                    {
                        for (int i = 0; i < row.Cells.Count; i++)
                        {
                            ICell cell = row.Cells[i];
                            if (!string.IsNullOrEmpty(cell.StringCellValue))
                                groups.Add(cell.StringCellValue);
                        }
                    }

                    if (data.isnew)
                    {
                        foreach (string g in groups)
                        {
                            List<int> indexs = new List<int>();
                            string[] arr = g.Split('|');
                            foreach (string dt in arr)
                            {
                                int index = data.keys.IndexOf(dt);
                                if (index == -1)
                                    return "找不到数据分组要的字段[" + dt + "]，SheetName = " + tableName + "，FileName = " + book.fileName;
                                indexs.Add(index);
                            }
                            data.groups.Add(g, arr);
                            data.groupindexs.Add(g, indexs.ToArray());
                        }
                    }
                    else
                    {
                        if (groups.Count != data.groups.Count)
                            return "数据分组声明不一致，SheetName = " + tableName + "，FileNames = " + string.Join(",", data.files);
                        foreach (string g in groups)
                            if (!data.groups.ContainsKey(g))
                                return "数据分组声明不一致，SheetName = " + tableName + "，FileNames = " + string.Join(",", data.files);
                    }
                }


                // 8、sheet第五行开始是表的数据，首字段不填写视为无效数据
                List<int> ids = data.ids;
                List<List<object>> dataContent = data.dataContent;
                for (int i = 4; i <= sheet.LastRowNum; i++)
                {
                    IRow row = sheet.GetRow(i);
                    if (row == null || row.FirstCellNum > 0 || row.GetCell(0).CellType == CellType.Blank)
                        continue;

                    List<object> values = new List<object>();
                    for (int j = 0; j < data.cols.Count; j++)
                    {
                        ICell cell = row.GetCell(data.cols[j]);
                        try
                        {
                            object codevalue = null;
                            if (book.evaluate && cell != null && cell.CellType == CellType.Formula)
                            {
                                book.evaluator.DebugEvaluationOutputForNextEval = true;
                                CellValue cellValue = book.evaluator.Evaluate(cell);
                                switch (data.types[j])
                                {
                                    case "int":
                                        codevalue = cellValue.CellType == CellType.Numeric ? Convert.ToInt32(cellValue.NumberValue) :
                                            string.IsNullOrEmpty(cellValue.StringValue) ? 0 : int.Parse(cellValue.StringValue); break;
                                    case "string":
                                        codevalue = string.IsNullOrEmpty(cellValue.StringValue) ? string.Empty : cellValue.StringValue; break;
                                    case "double":
                                        codevalue = cellValue.CellType == CellType.Numeric ? cellValue.NumberValue :
                                            string.IsNullOrEmpty(cellValue.StringValue) ? 0 : double.Parse(cellValue.StringValue); break;
                                }
                            }
                            else
                            {
                                switch (data.types[j])
                                {
                                    case "int":
                                        int num;
                                        codevalue = (cell == null || cell.CellType == CellType.Blank) ? 0 :
                                            cell.CellType == CellType.Numeric ? Convert.ToInt32(cell.NumericCellValue) :
                                            int.TryParse(cell.StringCellValue, out num) ? num : 0;
                                        break;
                                    case "string":
                                        codevalue = cell == null ? "" : cell.StringCellValue; break;
                                    case "double":
                                        codevalue = cell == null ? 0 : cell.NumericCellValue; break;
                                }
                            }
                            values.Add(codevalue);
                        }
                        catch (Exception ex)
                        {
                            Console.Write(ex);
                            return "数据格式有误， 第" + (cell.RowIndex + 1) + "行第" + (cell.ColumnIndex + 1) + "列， SheetName = " + tableName + "，FileNames = " + book.fileName;
                        }
                    }

                    int id = (int)values[0];
                    if (id == 0) // id=0忽略，方便公式生成id
                        continue;
                    if (ids.Contains(id))
                        return "索引冲突 [" + values[0] + "]，SheetName = " + tableName + "，FileNames = " + string.Join(",", data.files);
                    // 添加id
                    ids.Add((int)values[0]);
                    // 添加数据
                    dataContent.Add(values);
                }

                data.isnew = false;
                return string.Empty;
            }
        }

        public static string ReadDataXlsx()
        {
            datas = new Dictionary<string, DataStruct>();

            List<string> results = new List<string>();
            int totalCount = 0;

            foreach (var book in CustomWorkbook.allBooks)
            {
                if (book.type != CustomWorkbookType.ExportData)
                    continue;

                for (int i = 0; i < book.workbook.NumberOfSheets; i++)
                {
                    ISheet sheet = book.workbook.GetSheetAt(i);
                    ThreadPool.QueueUserWorkItem(o =>
                    {
                        string error = DealWithDataSheet(sheet, book);
                        lock (results)
                            results.Add(error);
                    });
                    totalCount++;
                }
            }

            while (results.Count < totalCount)
                Thread.Sleep(TimeSpan.FromSeconds(0.01));

            foreach (string error in results)
                if (!string.IsNullOrEmpty(error))
                    return error;

            return string.Empty;
        }

        public static string ReadFormulaXlsx(Func<ISheet, string> deal)
        {
            formulaContents = new Dictionary<string, string>();

            foreach (var book in CustomWorkbook.allBooks)
            {
                if (book.type != CustomWorkbookType.ExportFormal)
                    continue;

                for (int i = 0; i < book.workbook.NumberOfSheets; i++)
                {
                    string error = deal(book.workbook.GetSheetAt(i));
                    if (!string.IsNullOrEmpty(error))
                        return error;
                }
            }

            return string.Empty;
        }
    }
}