using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.IO;

namespace carLocation
{
    public class LogFile
    {
        // to log file.
        public static void Write(string text)
        {
            try
            {
                string filePath = "C:\\AppLog";
                FileStream fs;
                StreamWriter sw;
                DateTime dTime = DateTime.Now;


                if (!Directory.Exists(filePath + "\\Dpanel_log"))
                    Directory.CreateDirectory(filePath + "\\Dpanel_log");

                if (!Directory.Exists(filePath + "\\Dpanel_log\\" + DateTime.Today.ToString("yyyy-MM")))
                    Directory.CreateDirectory(filePath + "\\Dpanel_log\\" + DateTime.Today.ToString("yyyy-MM"));

                filePath = filePath + "\\Dpanel_log\\" + DateTime.Today.ToString("yyyy-MM") + "\\" + dTime.ToString("yyyyMMdd") + ".log";
                fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.None);
                sw = new StreamWriter(fs);


                sw.WriteLine(dTime.ToString("yyyy-MM-dd HH:mm:ss") + "   " + text + "\r");
                sw.Flush();
                sw.Close();
                fs.Close();
            }
            catch
            {

            }
        }
    

      public static void Error(string text)
        {
            try
            {
                string filePath = "C:\\AppLog";
                FileStream fs;
                StreamWriter sw;
                DateTime dTime = DateTime.Now;


                if (!Directory.Exists(filePath + "\\Dpanel_log"))
                    Directory.CreateDirectory(filePath + "\\Dpanel_log");

                if (!Directory.Exists(filePath + "\\Dpanel_log\\" + DateTime.Today.ToString("yyyy-MM")))
                    Directory.CreateDirectory(filePath + "\\Dpanel_log\\" + DateTime.Today.ToString("yyyy-MM"));

                filePath = filePath + "\\Dpanel_log\\" + DateTime.Today.ToString("yyyy-MM") + "\\" + dTime.ToString("yyyyMMdd") + ".log";
                fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.None);
                sw = new StreamWriter(fs);


                sw.WriteLine(dTime.ToString("yyyy-MM-dd HH:mm:ss") + "   " + text + "\r");
                sw.Flush();
                sw.Close();
                fs.Close();
            }
            catch
            {

            }
        }
    }


}