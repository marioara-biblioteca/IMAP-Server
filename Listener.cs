using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Text.RegularExpressions;

namespace IMAPServer
{
    public class Listener
    {
        System.IO.StreamReader reader;
        System.IO.StreamWriter writer;
        TcpClient client;

        private string currentUsername;
        private string currentFolder;

        #region Main and Constructor 
        public Listener(TcpClient client)
        {
            this.client = client;
            NetworkStream stream = client.GetStream();
            reader = new System.IO.StreamReader(stream);
            writer = new System.IO.StreamWriter(stream);
            writer.NewLine = "\r\n";
            writer.AutoFlush = true;
        }
        public static void Start()
        {
            //TcpListener listener = new TcpListener(IPAddress.Parse(SMTPServer.ListenerIP), SMTPServer.ListenerPort);
            TcpListener listener = new TcpListener(IPAddress.Any, IMAPServer.ListenerPort);

            listener.Start();
            while (true)
            {
                Listener handler = new Listener(listener.AcceptTcpClient());
                Thread thread = new System.Threading.Thread(new ThreadStart(handler.Run));
                thread.Start();
            }
        }
        #endregion
        public void Run()
        {
            try
            {
                wr("*", "OK details");
                currentUsername = null;
                IMAPMessage message = new IMAPMessage();
                bool isUserAuthenticated = false;
                for (; ; )
                {
                    string line = rd();
                    if (line == null)
                        break;
                    string[] tokens = line.Split(' ');
                    bool requiresAuthorization = false;
                    ProcessImapMessage(tokens);
                }
            }
            catch (Exception) { }
        }

        #region
        private void ProcessImapMessage(string[] tokens)
        {

            //tokens[0] - tag
            //tokens[1] - command
            //tokens[2]++  - restul informatiei
            switch (tokens[1])
            {
                case "CAPABILITY":
                    wr("*", "CAPABILITY IMAP4rev1 NAMESPACE UNSELECT CHILDREN SPECIAL-USE LIST-EXTENDED LIST-STATUS");
                    wr(tokens[0], "OK CAPABILITY completed");
                    break;
                case "LOGIN":
                    LoginResponse(tokens[0], tokens[2], tokens[3]);
                    break;
                case "AUTHENTICATE":
                    //  AuthenticateResponse(tokens[0], tokens[2], tokens[3], "");//trb sa iau emailul de undeva dar din cate vad nu apare in mesajele anterioare
                    break;
                case "NAMESPACE":
                    NamespaceResponse(tokens[0]);
                    break;
                case "LIST":
                    ListResponse(tokens[0], tokens[2], tokens[3]);
                    break;
                case "EXAMINE":
                    ExamineResponse(tokens[0], tokens[2]);
                    break;
                case "FETCH":
                    FetchResponse(tokens[0]);
                    break;
                case "UID":
                    UIDFetchResponse(tokens[0], tokens[3]);
                    break;
                case "UNSELECT":
                    wr(tokens[0], "OK return to authentificated state. Succes");
                    currentFolder = "";
                    break;
                default:
                    wr("*", "BAD");
                    break;
            }
        }
        private void LoginResponse(string tag, string username, string password)
        {

            if (CheckCredentials(username, password) == true)
            {

                wr(tag, "OK LOGIN completed");
                currentUsername = username;
            }
            else
            {
                wr(tag, "NO Logon failure: unknown user name or bad password");
            }
        }
        private void AuthenticateResponse(string tag, string how, string hashedPass, string email)
        {
            if (how == "PLAIN")
            {
                if (CheckAuthenticationCredentials(email.Split('@')[0], hashedPass))//problema aici ca e ceva hash-uit nu in plain text si nush ce alg de criptare e ca sa aplic peste parola din users.txt
                {
                    wr("*", "IMAP4rev1 UNSELECT IDLE NAMESPACE QUOUTA ID XLIST");
                    wr(tag, $"OK {email} authenticated (Success)");
                }
                else
                {
                    wr(tag, "NO [AUTHENTICATIONFAILED] Invalid credentials (Failure)");
                }
            }
            else
            {
                //whoops do nothing
            }
        }
        private void NamespaceResponse(string tag)
        {

            // personal namespace: INBOX cu delimitatorul "." (se acceseaza foldere: INBOX.sent)
            //NIL NIL -> pentru other's namespace si shared namespec (care nu le configuram)
            wr("*", "NAMESPACE ((\"\" \"/\")) NIL  NIL");
            //wr("*", "NAMESPACE ((\"\" \"/\")) NIL  NIL");
            wr(tag, "OK NAMESPACE completed");
        }
        private void ListResponse(string tag, string reference, string mailbox)
        {

            //daca ajugnem aici, inseamna ca e un client logat
            //ne intereseaza sa ii listam folderele la care are acces acel client
            //reference + mailbox -> calea in care cautam folderele (relativ la ce stie clientul, nu la PC-ul serverului)

            if (reference == "(SPECIAL-USE)")
            {

                wr("*", "LIST (\\HasNoChildren \\Sent \\Subscribed) \"/\" \"sent\"");
            }
            else
                wr("*", "LIST () \"/\" \"INBOX\"");

            wr(tag, "OK LIST completed");
        }
        private void ExamineResponse(string tag, string folder)
        {
            DirectoryInfo inboxDirectory;
            if (folder == "sent")
            {
                string temp = Path.Combine(IMAPServer.InboxPath, currentUsername);
                inboxDirectory = new DirectoryInfo((Path.Combine(temp, "sent")));
                currentFolder = "sent";
            }
            else
            {
                inboxDirectory = new DirectoryInfo(Path.Combine(IMAPServer.InboxPath, currentUsername));
                currentFolder = "inbox";
            }
            FileInfo[] mails = inboxDirectory.GetFiles();

            int ExistsNo = mails.Length;
            int UIDnext = 0;
            foreach (var mail in mails)
            {

                string localUID = mail.Name;
                int dotPos = mail.Name.IndexOf('.');
                if (dotPos != -1)
                    localUID = mail.Name.Substring(0, dotPos);

                if (Int32.Parse(localUID) > UIDnext)
                    UIDnext = Int32.Parse(localUID);
            }

            wr("*", "FLAGS (\\Answered \\Flagged \\Draft \\Deleted \\Seen)");
            wr("*", "OK [PERMANENTFLAGS ()] No permanent flags permitted.");
            wr("*", "OK [UIDVALIDITY 7] UIDs valid.");      //un numar folosit la sincronizarea UID-urilor dintre client si server (daca nu se schimba de la un EXAMINE la altul -> sicnronizarea e okay)
            wr("*", ExistsNo.ToString() + " EXISTS");            //cate mailuri sunt in acest mailbox
            wr("*", "0 RECENT");            //cate au aparut de la ultima interogare EXAMINE
            //wr("*", "OK[UNSEEN 1] Message 1 is first unseen");  // asta daca exista mesaje RECENTE!!! -> UID-ul la primul in acest caz
            wr("*", "OK [UIDNEXT " + UIDnext.ToString() + "] Predicted next UID.");       // UID pt ultimul mail + 1
            wr("*", "OK [HIGHESTMODSEQ 245306]");
            wr(tag, "OK [READ-ONLY] INBOX selected. (Success)");
        }

        private void FetchResponse(string tag)
        {

            string inboxDirectoryPath = Path.Combine(IMAPServer.InboxPath, currentUsername);
            if (currentFolder != "inbox")
                inboxDirectoryPath = Path.Combine(inboxDirectoryPath, currentFolder);
            DirectoryInfo inboxDirectory = new DirectoryInfo(inboxDirectoryPath);
            FileInfo[] mails = inboxDirectory.GetFiles();
            string fetchResponse;
            int counter = 1;
            foreach (var mail in mails)
            {

                string messagePath = Path.Combine(inboxDirectoryPath, mail.Name);
                MimeKit.MimeMessage message = MimeKit.MimeMessage.Load(messagePath);

                int octetsNo = message.TextBody.Length;
                int lines = Regex.Matches(message.TextBody, "\\n").Count;

                string localUID = mail.Name;
                int dotPos = mail.Name.IndexOf('.');
                if (dotPos != -1)
                    localUID = mail.Name.Substring(0, dotPos);

                fetchResponse = $"{counter.ToString()} FETCH (UID {localUID}";

                string bodystructure = GetBodystructureForMail(message);
                fetchResponse += bodystructure;

                string envelope = GetEnvelopeForMessage(message);
                fetchResponse += envelope;

                fetchResponse += ")";

                wr("*", fetchResponse);
                counter++;
            }
            wr(tag, "OK Succes");
        }

        private string GetBodystructureForMail(MimeKit.MimeMessage message)
        {

            string result = " BODYSTRUCTURE (";

            string bodyPartString;
            string boundary;
            foreach (var bodyPart in message.BodyParts)
            {


                string mediaType = bodyPart.ContentType.MediaType;
                string mediaSubtype = bodyPart.ContentType.MediaSubtype;

                string text;

                if (mediaSubtype == "plain")
                {
                    text = message.TextBody;
                }
                else if (mediaSubtype == "html")
                {
                    text = message.HtmlBody;

                }
                else
                    throw new Exception("MEDIA SUBTYPE NECUNOSCUT");

                int linesNo = Regex.Matches(text, "\n").Count; //asta nu stiu exact ce e cu el. Ca nu da ca la exemplu de la gmail ???

                bodyPartString = $"(\"{mediaType.ToUpper()}\" \"{mediaSubtype.ToUpper()}\" (\"CHARSET\" \"{bodyPart.ContentType.Charset}\") NIL NIL \"7BIT\" {text.Length} {linesNo} NIL NIL NIL)";
                result += bodyPartString;

            }

            result += $"\"ALTERNATIVE\" (\"BOUNDARY\" \"{message.Body.ContentType.Boundary}\") NIL NIL)";

            return result;
        }
        private string GetEnvelopeForMessage(MimeKit.MimeMessage message)
        {

            //FROM      (("Razvan O." NIL "ogreanrazvan" "gmail.com"))
            string from;
            if (message.From != null && message.From.Count > 0)
            {
                from = "(";
                foreach (var mailbox in message.From.Mailboxes)
                {

                    from += GetAddresStructure(mailbox);
                }
                from += ")";
            }
            else
                from = "NIL";

            //SENDER
            string sender = "(";
            if (message.Sender != null)
            {
                MimeKit.MailboxAddress test = new MimeKit.MailboxAddress(message.Sender.Encoding, message.Sender.Name, message.Sender.Route, message.Sender.Address);
                sender += GetAddresStructure(test);
                sender += ")";
            }
            else
                sender = "NIL";

            //reply-to
            string replyto;
            if (message.ReplyTo != null && message.ReplyTo.Count > 0)
            {
                replyto = "(";
                foreach (var mailbox in message.ReplyTo.Mailboxes)
                {

                    replyto += GetAddresStructure(mailbox);
                }
                replyto += ")";
            }
            else
                replyto = "NIL";

            //to
            string to;
            if (message.To != null && message.To.Count > 0)
            {
                to = "(";
                foreach (var mailbox in message.To.Mailboxes)
                {

                    to += GetAddresStructure(mailbox);
                }
                to += ")";
            }
            else
                to = "NIL";

            //cc
            string cc;
            if (message.Cc != null && message.Cc.Count > 0)
            {
                cc = "(";
                foreach (var mailbox in message.Cc.Mailboxes)
                {

                    cc += GetAddresStructure(mailbox);
                }
                cc += ")";
            }
            else
                cc = "NIL";

            //bcc
            string bcc;
            if (message.Bcc != null && message.Bcc.Count > 0)
            {
                bcc = "(";
                foreach (var mailbox in message.Bcc.Mailboxes)
                {

                    bcc += GetAddresStructure(mailbox);
                }
                bcc += ")";
            }
            else
                bcc = "NIL";

            //in-reply-to
            string inReplyTo = (message.InReplyTo != null) ? message.InReplyTo : "NIL";

            //messageID
            string messageID = message.MessageId.ToString();


            string result = "ENVELOPE (";
            result += $"\"{message.Date.ToString()}\" \"{message.Subject}\" {from} {sender} {replyto} {to} {cc} {bcc} {inReplyTo} \"<{messageID}>\""; // date + subject
            result += ")";

            return result;
        }
        private string GetAddresStructure(MimeKit.MailboxAddress mailbox)
        {

            string result = "(";
            result += $"\"{mailbox.Name}\"";

            string route = (mailbox.Route.ToString() == "" || mailbox.Route == null) ? "NIL" : mailbox.Route.ToString();
            result += $" {route}";

            string mailboxAddres = mailbox.Address.ToString(); //ogreanrazvan@gmail.com
            string mailboxName = IMAPMessage.extracUserFromMail(mailboxAddres); //ogreanrazvan
            string mailboxHost = IMAPMessage.extractHostFromMail(mailboxAddres); //gmail.com
            result += $" \"{mailboxName}\" \"{mailboxHost}\"";

            result += ")";
            return result;
        }


        private void UIDFetchResponse(string tag, string UID)
        {

            string inboxDirectoryPath = Path.Combine(IMAPServer.InboxPath, currentUsername);
            string mailPath = Path.Combine(inboxDirectoryPath, UID + ".txt");

            MimeKit.MimeMessage message = MimeKit.MimeMessage.Load(mailPath);

            //rd();

            wr("*", "1 FETCH (UID 1 BODY[1] {27}"); //lin1 lin2 lin3 pa lin4 BODY[1.MIME] {45} Content-Type: text/plain; charset=\"UTF - 8\" )");
            wr("", "lin1");
            wr("", "lin2");
            wr("", "lin3");
            wr("", "pa lin4");
            wr("", "BODY[1.MIME] {45}");
            wr("", "Content-Type: text/plain; charset=\"UTF-8\"");
            wr("", "");
            wr("", ")");
            wr(tag, "OK succes");
        }
        private bool CheckCredentials(string username, string password)
        {
            //verifica daca exista in fisier acest user

            string[] credentials;
            foreach (var line in File.ReadAllLines(IMAPServer.fileCredentials))
            {

                credentials = line.Split(" ");
                if (credentials[0] == username && credentials[1] == password)
                    return true;
            }
            return false;
        }
        private bool CheckAuthenticationCredentials(string username, string hashedPassword)
        {
            string[] credentials;
            foreach (var line in File.ReadAllLines(IMAPServer.fileCredentials))
            {

                credentials = line.Split(" ");
                if (credentials[0] == username) //&& algorithDeHashPeCareNuIlStim(credentials[1]==hashedPassword)
                    return true;
            }
            return false;
        }
        #endregion
        private void wr(string tag, string c)
        {
            //writer.WriteLine(code + " " + c);
            writer.WriteLine(tag + " " + c);
            writer.Flush();
            Console.WriteLine("S: " + tag + " " + c);
        }
        private string rd()
        {
            string result = null;
            try
            {
                //Console.WriteLine(reader.EndOfStream);
                result = reader.ReadLine();
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: " + e.Message);
            }
            if (result == null)
                Console.WriteLine("C: NULL");
            else
            {

                Console.WriteLine("C: " + result);
                File.AppendAllText("mailHeader.txt", result);
                File.AppendAllText("mailHeader.txt", "\n");
            }
            return result;
        }
        private string rd64()
        {
            try
            {
                string record = rd();
                if (record == null)
                    return null;
                return System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(record));
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}