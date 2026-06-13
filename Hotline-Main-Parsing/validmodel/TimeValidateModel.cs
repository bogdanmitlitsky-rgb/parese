using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hotline_Main_Parsing.validmodel
{
    public class TimeValidateModel : IDataErrorInfo
    {
        public int BrowserCount { get; set; }
        public string StartTime { get; set; }
        public string StopTime { get; set; }
        public string this[string columnName]
        {
            get
            {
                
                string error = String.Empty;    
                switch (columnName)
                {
                    case "BrowserCount":
                        try
                        {
                            int number;

                            int.TryParse(BrowserCount.ToString(), out number);
                        
                        }
                        catch(Exception ex)
                        {
                            error = "Количество браузеров должно быть больше 0";

                        }
                      
                        break;
                    case "StartTime":
                        try
                        {
                            if (!string.IsNullOrEmpty(StartTime))
                                CheckTime(StartTime);
                        }
                        catch (Exception ex)
                        {
                            error = "Время указано не правильно";
                        }
                        break;
                    case "StopTime":
                        try
                        {
                            if (!string.IsNullOrEmpty(StopTime))
                                CheckTime(StopTime);
                        }
                        catch (Exception ex)
                        {
                            error = "Время указано не правильно";
                        }
                        break;
                }
                return error;
            }
        }

        private void CheckTime(string time)
        {
            TimeSpan ts = new TimeSpan();
            ts = TimeSpan.Parse(time);
            if (ts.Days > 0)
                throw new Exception("Время указано не правильно");
            Console.WriteLine(ts);
        }
        public string Error
        {
            get { throw new NotImplementedException(); }
        }
    }
}
