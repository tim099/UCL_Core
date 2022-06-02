using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UCL.Core.CsvLib {
    [System.Serializable]
    public class CSVRowData {
        
        public int Count {
            get {
                if(m_Columes == null) return 0;
                return m_Columes.Count;
            }
        }
        public CSVRowData()
        {

        }
        public CSVRowData(string iData) {
            var columes = iData.Split(new[] { ',' });//, System.StringSplitOptions.RemoveEmptyEntries
            for(int i = 0; i < columes.Length; i++) {
                m_Columes.Add(columes[i]);
            }
        }
        public void AddColume(string iValue)
        {
            m_Columes.Add(iValue);
        }
        public string Get(int iColume) {
            if(iColume < 0 || iColume >= m_Columes.Count) return string.Empty;
            return m_Columes[iColume];
        }
        public List<string> m_Columes = new List<string>();
    }
    [System.Serializable]
    public class CSVData {
        /// <summary>
        /// Row count
        /// </summary>
        public int Count => m_Rows == null ? 0 : m_Rows.Count;
        public CSVData()
        {

        }
        public CSVData(string iData) {
            var aSplitLineRegex = new Regex(@"\r\n", RegexOptions.Compiled);
            var aRows = aSplitLineRegex.Split(iData);//.SplitByLine();
            for(int i = 0; i < aRows.Length; i++) {
                if(!string.IsNullOrEmpty(aRows[i])) {
                    m_Rows.Add(new CSVRowData(aRows[i]));
                }
            }
        }
        public string ToCSV()
        {
            System.Text.StringBuilder aSB = new System.Text.StringBuilder();
            for(int i = 0; i < m_Rows.Count; i++)
            {
                var aRow = m_Rows[i];
                for(int j = 0; j < aRow.Count; j++)
                {
                    aSB.Append(aRow.Get(j));
                    aSB.Append(',');
                }
                aSB.Append(@"\r\n");
            }
            return aSB.ToString();
        }
        public CSVRowData AddRow()
        {
            var aRow = new CSVRowData();
            m_Rows.Add(aRow);
            return aRow;
        }
        public string GetData(int iRow, int iColume) {
            if(iRow < 0 || iRow >= m_Rows.Count) return string.Empty;
            return m_Rows[iRow].Get(iColume);
        }
        public CSVRowData GetRow(int iRow) {
            if(iRow < 0 || iRow >= m_Rows.Count) return null;
            return m_Rows[iRow];
        }
        public List<CSVRowData> m_Rows = new List<CSVRowData>();
    }
}
