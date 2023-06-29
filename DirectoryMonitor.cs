using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace DirectoryMonitor
{
    public partial class DirectoryMonitor : ServiceBase
    {

        public DirectoryMonitor()
        {
            InitializeComponent();
        }

        FileSystemWatcher fileSystemWatcher;
        string sftppath = ConfigurationManager.AppSettings["SFTP_Path"];
        int clientFolderRootCount = Convert.ToInt32(ConfigurationManager.AppSettings["ClientFolderRootCount"].ToString());
        string connection = System.Configuration.ConfigurationManager.ConnectionStrings["SQLServerConnection"].ConnectionString;
        bool sendMail = Convert.ToBoolean(ConfigurationManager.AppSettings["SendMail"]);
        string dateLoadFileExtention = ConfigurationManager.AppSettings["DataLoadFileExtention"];
        string sftpUnknownErrorPath = "";



        /// <summary>
        /// File watcher onstart
        /// </summary>
        /// <param name="args"></param>
        protected override void OnStart(string[] args)
        {
            
            //System.Diagnostics.Debugger.Launch();
            ConfigurationManager.RefreshSection("appSettings");
            EventLog.WriteEntry("Directory Monitor Started");
            fileSystemWatcher = new FileSystemWatcher(sftppath);

            fileSystemWatcher.EnableRaisingEvents = true;
            fileSystemWatcher.IncludeSubdirectories = true;
            fileSystemWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                                             NotifyFilters.Security | NotifyFilters.CreationTime | NotifyFilters.LastAccess |
                                             NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.FileName;

            fileSystemWatcher.Created += DirectoryChanged;
            
        }


        /// <summary>
        /// It will trigger if any changes in directory
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DirectoryChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                //string[] extensions = { ".zip", ".csv" };
                var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLower();
                if (dateLoadFileExtention.ToLower().Equals(ext.ToLower()))
                {

                    var msg = $"{DateTime.Now} - {e.ChangeType} - {e.Name}{System.Environment.NewLine}";
                    string[] fullPathList = e.FullPath.Split(Path.DirectorySeparatorChar);
                    string[] nameList = e.Name.Split(Path.DirectorySeparatorChar);
                    bool isMainDirectory = true;
                    if (fullPathList != null && fullPathList.Length > 0)
                    {
                        isMainDirectory = fullPathList.Count() - 1 == clientFolderRootCount ? true : false;
                    }

                    bool isSubDirectory = fullPathList.Contains("Archive") || fullPathList.Contains("Template") || fullPathList.Contains("DataFeedValidation") || fullPathList.Contains("Utils");
                    var servicelocation = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
                    if (e.ChangeType.ToString() == "Created" && !isSubDirectory && isMainDirectory)
                    {
                        var clientName = nameList != null || nameList.Count() != 0 ? nameList[0] : "NoClientCode";
                        string logFolderName = "Logs";
                        string clientLogPath = sftppath + clientName + "\\" + logFolderName;
                        if (!Directory.Exists(clientLogPath))
                        {
                            Directory.CreateDirectory(clientLogPath);
                        }
                        string logfilePath = clientLogPath + "\\" + clientName + "_" + ConfigurationManager.AppSettings["LogFile"].ToString();
                        sftpUnknownErrorPath = logfilePath;

                        File.AppendAllText($"{logfilePath}", msg);
                        if (Convert.ToBoolean(ConfigurationManager.AppSettings["CheckClientCodePrefixOnfileName"].ToString()))
                        {
                            if (nameList[1].ToLower().StartsWith(clientName.ToLower()+"_"))
                            {
                                RunSSISPackage(e, clientName, logfilePath);
                            }
                            else
                            {
                                var message = $"{DateTime.Now} - {e.Name} - New file received but not able to proces because the filename not having the client code prefix. The file name should starts with client code like => {clientName}_.{System.Environment.NewLine}";
                                File.AppendAllText($"{logfilePath}", message);
                                if(sendMail)
                                SendAutomatedEmail(clientName, logfilePath, message, e);
                            }
                        }
                        else
                        {
                            RunSSISPackage(e, clientName, logfilePath);
                        }                     
                    }
                }
            }
            catch (Exception ex)
            {
                string message = $"{DateTime.Now} - Something wrong during directory monitor process.Please check below error message.{System.Environment.NewLine}";
                File.AppendAllText($"{sftpUnknownErrorPath}", message);
                File.AppendAllText($"{sftpUnknownErrorPath}", ex.Message);
                if(sendMail)
                SendAutomatedEmail("Unknown", sftpUnknownErrorPath, message+" "+ ex.Message, e);
            }
        }


        /// <summary>
        /// Run Data Load SSIS package using SQL Job
        /// </summary>
        /// <param name="e">file watcher event</param>
        /// <param name="clientName">cleintname</param>
        /// <param name="logfilePath">client log file path</param>
        private void RunSSISPackage(FileSystemEventArgs e, string clientName, string logfilePath)
        {

            string message = "";
            try
            {

                var jobName = ConfigurationManager.AppSettings["SSIS_SQL_Job_Prefix"].ToString()+"_" + clientName+"_"+ ConfigurationManager.AppSettings["SSIS_SQL_Job_Suffix"].ToString();
                bool isPass = GetJobsAndStatus(jobName, logfilePath, clientName, e);
                if (isPass)
                {
                    using (SqlConnection sqlCon = new SqlConnection(connection))
                    {
                        sqlCon.Open();
                        SqlCommand cmd = new SqlCommand("msdb.dbo.sp_start_job", sqlCon);
                        var triggermsg = $" SSIS Trigger Started at {DateTime.Now} for file - {e.FullPath}{System.Environment.NewLine}";
                        File.AppendAllText($"{logfilePath}", triggermsg);
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Parameters.AddWithValue("@job_name", jobName);
                        cmd.ExecuteNonQuery();
                        sqlCon.Close();
                        var triggerendmsg = $" SSIS Job Trigger End at {DateTime.Now}{System.Environment.NewLine}";
                        File.AppendAllText($"{logfilePath}", triggerendmsg);
                    }
                }
            }
            catch (Exception ex)
            {
                message = $"{DateTime.Now} - Something wrong during the SSIS SQL Job execution process.Please check below the error message.{System.Environment.NewLine}";
                File.AppendAllText($"{logfilePath}",message);
                File.AppendAllText($"{logfilePath}", ex.Message);
                if(sendMail)
                SendAutomatedEmail(clientName, logfilePath, message + " " + ex.Message, e);
            }

        }


        /// <summary>
        /// This will read the SQL SSIS Job list and check the respective client service details.
        /// </summary>
        /// <param name="jobName">ssis job name</param>
        /// <param name="logfilePath">log file path</param>
        /// <param name="clientName">client code</param>
        /// <param name="e">file watcher event</param>
        /// <returns>will return true if job is valid</returns>
        public bool GetJobsAndStatus(string jobName, string logfilePath,string clientName, FileSystemEventArgs e)
        {

            bool isValidJob = false;
            string message = "";
            string sqlJobQuery = "SELECT [name],[Enabled] FROM msdb.dbo.sysjobs";
            List<sqlJob> sqlJoblist = new List<sqlJob>();
            using (SqlConnection _con = new SqlConnection(connection))
            using (SqlCommand _cmd = new SqlCommand(sqlJobQuery, _con))
            {
                try
                {
                    _con.Open();
                    SqlConnection.ClearPool(_con);
                    using (SqlDataReader rdr = _cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            sqlJoblist.Add(new sqlJob { jobName = rdr.GetString(0), isEnabled = rdr.GetByte(1)});
                        }
                        rdr.Close();
                    }                }
                catch (Exception ex)
                {
                    message = $"{DateTime.Now} - Something wrong during the JobList reader execution process. Please check the error message.{System.Environment.NewLine}";
                    File.AppendAllText($"{logfilePath}",message);
                    File.AppendAllText($"{logfilePath}", ex.Message);
                    if(sendMail)
                    SendAutomatedEmail(clientName, logfilePath, message + " " + ex.Message, e);
                    isValidJob = false;
                }
            }

            if(sqlJoblist.Count > 0)
            {
                var job = sqlJoblist.Where(x => x.jobName.ToLower() == jobName.ToLower()).FirstOrDefault();
                if(job != null)
                {
                    if (job.isEnabled == 1)
                    {
                        message = $"{DateTime.Now} - SSIS SQL Job Name - {jobName} -  is exist in sql server agent and already enabled.We can perform the data load.  {System.Environment.NewLine}";
                        File.AppendAllText($"{logfilePath}", message);
                        isValidJob = true;
                        
                    }
                    else
                    { 
                        message = $"{DateTime.Now} - SSIS SQL Job Name - {jobName} -  is exist in sql server agent and job is not enabled.We can not perform the data load further.  {System.Environment.NewLine}";
                        File.AppendAllText($"{logfilePath}", message);
                        if(sendMail)
                        SendAutomatedEmail(clientName, logfilePath, message, e);
                        isValidJob = false;
                    }
                }
                else
                {
                    
                    message = $"{DateTime.Now} - SQL Job for SSIS load is not exist in sql server agent. Please setup the same for data load. Here SFTP's client folder name is consider as a client code. The SQL job name should be -> {jobName}.{System.Environment.NewLine}";
                    File.AppendAllText($"{logfilePath}", message);
                    if(sendMail)
                    SendAutomatedEmail(clientName, logfilePath, message, e);
                    isValidJob = false;
                }
            }
            else
            {
                message = $"{DateTime.Now} - SQL Job for SSIS load is not exist in sql server agent. Please setup the same for data load. Here SFTP's client folder name is consider as a client code. The SQL job name should be -> {jobName}.{System.Environment.NewLine}";
                File.AppendAllText($"{logfilePath}", message);
                if(sendMail)
                SendAutomatedEmail(clientName, logfilePath, message, e);
                isValidJob = false;
            }

            return isValidJob;
        }


        /// <summary>
        /// Will send the mail if any issue with the SSIS job execution
        /// </summary>
        /// <param name="clientName">client name</param>
        /// <param name="logfilePath"> client log path</param>
        /// <param name="messagInfo"> reason for ssis job fail</param>
        /// <param name="e">file watcher event</param>
        public void SendAutomatedEmail(string clientName, string logfilePath,string messagInfo, FileSystemEventArgs e)
        {
          
            try 
            { 
            string mailServer = ConfigurationManager.AppSettings["Mail_SMTP_Host"];
            string toMailAddresses = ConfigurationManager.AppSettings["Mail_SMTP_ToEmails_Error"];
            string fromMailAddress = ConfigurationManager.AppSettings["Mail_SMTP_FromEmail"];
            string environment = ConfigurationManager.AppSettings["Setup_Environment"];
            string product = ConfigurationManager.AppSettings["Product"];
            string mailSplitChar = ConfigurationManager.AppSettings["Product"].ToString();
            MailMessage message = new MailMessage();
            message.From = new MailAddress(fromMailAddress);
            foreach (var address in toMailAddresses.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries))
            {
                message.To.Add(address);
            }
            message.IsBodyHtml = true;
            message.Body = "Hello Team, We have received a new data load file and the details below,<br/><br/>"
                                + "<b>Product: </b>" + product + "<br/>"
                                + "<b>Environment: </b>" + environment + "<br/>"
                                + "<b>File Name: </b>" + e.Name + "<br/>"
                                + "<b>File Path: </b>" + e.FullPath + "<br/>"
                                + "<b>Service Information: </b>" + messagInfo + "<br/>"
                                + "<b>For Logs: </b>" + logfilePath
                                + "<br/><br/> Regards,<br/> "                              
                                + "Team "+ product+"<br/>"
                                + environment +" - sftp file monitor service<br/>";
                            
            message.Subject =  "["+ environment + "] - "+ clientName +" - New Data Load File Received Notification.";
            SmtpClient client = new SmtpClient(mailServer);
            client.Port = Convert.ToInt32(ConfigurationManager.AppSettings["Mail_SMTP_Port"].ToString());
            client.EnableSsl = true;
            client.UseDefaultCredentials = true;
            var AuthenticationDetails = new NetworkCredential(ConfigurationManager.AppSettings["Mail_SMTP_UserName"].ToString(), ConfigurationManager.AppSettings["Mail_SMTP_Password"].ToString());
            client.Credentials = AuthenticationDetails;
            client.Send(message);
            }
            catch(Exception ex)
            {
                string message = $"{DateTime.Now} - Something wrong during the mail sending process. Please check the error message.{System.Environment.NewLine}";
                File.AppendAllText($"{logfilePath}", message);
                File.AppendAllText($"{logfilePath}", ex.Message);
            }
        }

        protected override void OnStop()
        {
            // TODO: Add code here to perform any tear-down necessary to stop your service.
        }

        public class sqlJob
        {
            public string jobName { get; set; }
            public byte isEnabled { get; set; }
        }
    }
}
