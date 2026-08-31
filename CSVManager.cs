using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Diagnostics;

namespace G2C.Invoice
{
   
    public enum EmptyLineBehavior
    {
       
        NoColumns,

        
        EmptyColumn,

       
        Ignore,

        
        EndOfFile,
    }

    
    public abstract class CsvFileCommon
    {
       
        protected char[] SpecialChars = new char[] { ',', '"', '\r', '\n' };

        
        private const int DelimiterIndex = 0;
        private const int QuoteIndex = 1;

        
        public char Delimiter
        {
            get { return SpecialChars[DelimiterIndex]; }
            set { SpecialChars[DelimiterIndex] = value; }
        }

        
        public char Quote
        {
            get { return SpecialChars[QuoteIndex]; }
            set { SpecialChars[QuoteIndex] = value; }
        }
    }

    
    public class CsvFileReader : CsvFileCommon, IDisposable
    {
     
        private StreamReader Reader;
        private string CurrLine;
        private int CurrPos;
        private EmptyLineBehavior EmptyLineBehavior;

       
        public CsvFileReader(Stream stream,
            EmptyLineBehavior emptyLineBehavior = EmptyLineBehavior.NoColumns)
        {
            Reader = new StreamReader(stream);
            EmptyLineBehavior = emptyLineBehavior;
        }

       
        public CsvFileReader(string path,
            EmptyLineBehavior emptyLineBehavior = EmptyLineBehavior.NoColumns)
        {
            Reader = new StreamReader(path);
            EmptyLineBehavior = emptyLineBehavior;
        }

        
        public bool ReadRow(List<string> columns)
        {
            
            if (columns == null)
                throw new ArgumentNullException(nameof(columns));

            ReadNextLine:
           
            CurrLine = Reader.ReadLine();
            CurrPos = 0;
          
            if (CurrLine == null)
                return false;
         
            if (CurrLine.Length == 0)
            {
                switch (EmptyLineBehavior)
                {
                    case EmptyLineBehavior.NoColumns:
                        columns.Clear();
                        return true;
                    case EmptyLineBehavior.Ignore:
                        goto ReadNextLine;
                    case EmptyLineBehavior.EndOfFile:
                        return false;
                }
            }

            // Parse line
            string column;
            int numColumns = 0;
            while (true)
            {
                // Read next column
                if (CurrPos < CurrLine.Length && CurrLine[CurrPos] == Quote)
                    column = ReadQuotedColumn();
                else
                    column = ReadUnquotedColumn();
                // Add column to list
                if (numColumns < columns.Count)
                    columns[numColumns] = column;
                else
                    columns.Add(column);
                numColumns++;
                // Break if we reached the end of the line
                if (CurrLine == null || CurrPos == CurrLine.Length)
                    break;
                // Otherwise skip delimiter
                Debug.Assert(CurrLine[CurrPos] == Delimiter);
                CurrPos++;
            }
            // Remove any unused columns from collection
            if (numColumns < columns.Count)
                columns.RemoveRange(numColumns, columns.Count - numColumns);
            // Indicate success
            return true;
        }

      
        private string ReadQuotedColumn()
        {
            // Skip opening quote character
            Debug.Assert(CurrPos < CurrLine.Length && CurrLine[CurrPos] == Quote);
            CurrPos++;

            // Parse column
            StringBuilder builder = new StringBuilder();
            while (true)
            {
                while (CurrPos == CurrLine.Length)
                {
                    // End of line so attempt to read the next line
                    CurrLine = Reader.ReadLine();
                    CurrPos = 0;
                    // Done if we reached the end of the file
                    if (CurrLine == null)
                        return builder.ToString();
                    // Otherwise, treat as a multi-line field
                    builder.Append(Environment.NewLine);
                }

                // Test for quote character
                if (CurrLine[CurrPos] == Quote)
                {
                    // If two quotes, skip first and treat second as literal
                    int nextPos = (CurrPos + 1);
                    if (nextPos < CurrLine.Length && CurrLine[nextPos] == Quote)
                        CurrPos++;
                    else
                        break;  // Single quote ends quoted sequence
                }
                // Add current character to the column
                builder.Append(CurrLine[CurrPos++]);
            }

            if (CurrPos < CurrLine.Length)
            {
                // Consume closing quote
                Debug.Assert(CurrLine[CurrPos] == Quote);
                CurrPos++;
                // Append any additional characters appearing before next delimiter
                builder.Append(ReadUnquotedColumn());
            }
            // Return column value
            return builder.ToString();
        }

        
        private string ReadUnquotedColumn()
        {
            int startPos = CurrPos;
            CurrPos = CurrLine.IndexOf(Delimiter, CurrPos);
            if (CurrPos == -1)
                CurrPos = CurrLine.Length;
            if (CurrPos > startPos)
                return CurrLine.Substring(startPos, CurrPos - startPos);
            return String.Empty;
        }

        // Propagate Dispose to StreamReader
        public void Dispose()
        {
            Reader.Dispose();
        }
    }

    public class CsvFileWriter : CsvFileCommon, IDisposable
    {
        // Private members
        private StreamWriter Writer;
        private string OneQuote = null;
        private string TwoQuotes = null;
        private string QuotedFormat = null;


        /// <param name="stream">The stream to write to</param>
        public CsvFileWriter(Stream stream)
        {
            Writer = new StreamWriter(stream);
        }


        /// <param name="path">The name of the CSV file to write to</param>
        public CsvFileWriter(string path)
        {
            Writer = new StreamWriter(path);
        }


        /// <param name="columns">The list of columns to write</param>
        public void WriteRow(List<string> columns)
        {
            // Verify required argument
            if (columns == null)
                throw new ArgumentNullException(nameof(columns));

            // Ensure we're using current quote character
            if (OneQuote == null || OneQuote[0] != Quote)
            {
                OneQuote = String.Format("{0}", Quote);
                TwoQuotes = String.Format("{0}{0}", Quote);
                QuotedFormat = String.Format("{0}{{0}}{0}", Quote);
            }

            // Write each column
            for (int i = 0; i < columns.Count; i++)
            {
                // Add delimiter if this isn't the first column
                if (i > 0)
                    Writer.Write(Delimiter);
                // Write this column
                if (columns[i].IndexOfAny(SpecialChars) == -1)
                    Writer.Write(columns[i]);
                else
                    Writer.Write(QuotedFormat, columns[i].Replace(OneQuote, TwoQuotes));
            }
            Writer.WriteLine();
        }

        // Propagate Dispose to StreamWriter
        public void Dispose()
        {
            Writer.Dispose();
        }
    }
}
