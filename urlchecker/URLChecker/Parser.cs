using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using URLChecker.Properties;

namespace URLChecker
{
    [Serializable]
    public class Parser
    {
        [NonSerialized]
        public int[] _pos;

        [NonSerialized]
        public int[] _filtered;

        [NonSerialized]
        public string[] _tmp;         //Фильтр

        public bool haveBroken = false;

        public string name;         //Имя проекта

        public string domen;

        public bool stop = false;

        public filter ActiveFilter = new filter();      //Фильтр

        int last_num;

        [NonSerialized]
        Network net;

        //Пдключение пользователя
        public delegate void ParsedLink(Link link);

        public List<Link> links = new List<Link>();

        public Parser()
        {
            net = new Network();
            last_num = 0;
        }

        public void AddLink(Link link)
        {
            last_num++;
            link.unique = last_num;
            link.checkedURL = domen;
            links.Add(link);
            _pos = new int[links.Count];
            //_tmp = new int[links.Count];
            for (int x = 0; x < links.Count; x++)
            {
                _pos[x] = x;
                links[x].N_ = x + 1;
            }

        }

        public void Sorting(int indexCol, bool asc)
        {
            _pos = new int[links.Count];
            _tmp = new string[links.Count];

            for (int x = 0; x < links.Count; x++)
            {
                _pos[x] = x;
                switch (indexCol)
                {
                    case 0: _tmp[x] = Convert.ToString(links[x].N_); break;
                    case 1: _tmp[x] = links[x].url; break;
                    case 2: _tmp[x] = links[x].url; break;
                    case 3: _tmp[x] = links[x].backURL; break;
                    case 4: _tmp[x] = links[x].anchor; break;
                    case 5: _tmp[x] = Convert.ToString(links[x].noindex); break;
                    case 6: _tmp[x] = Convert.ToString(links[x].nofollow); break;
                    case 7: _tmp[x] = Convert.ToString(links[x].noYR); break;
                    case 8: _tmp[x] = Convert.ToString(links[x].noGR); break;
                }                
            }

            if (asc)
                if (indexCol ==0)
                    Array.Sort(_tmp, _pos, new ComparerAsc());
                else
                    Array.Sort(_tmp, _pos, new StringComparerAsc());
            else
                if (indexCol == 0)
                    Array.Sort(_tmp, _pos, new ComparerDesc());
                else
                    Array.Sort(_tmp, _pos, new StringComparerDesc());
        } 


        public void DeleteLink(int pos)
        {
            links[_pos[pos]].deleted = true;                
        }

        public void Rebuild()
        {
            for (int i = links.Count-1; i >= 0; i--)
            {
                if (links[i].deleted)
                    links.RemoveAt(i);
            }
            _pos = new int[links.Count];
            for (int x = 0; x < links.Count; x++)
            {
                _pos[x] = x;
                links[x].N_ = x + 1;
            }
        }

        public void DeleteLinkUnique(int unique)
        {            
            for (int i = 0; i < links.Count; i++)
            {
                if (links[i].unique == unique)
                    links.RemoveAt(i);
            }
            _pos = new int[links.Count];
            for (int x = 0; x < links.Count; x++)
            {
                _pos[x] = x;
                links[x].N_ = x + 1;
            }
            ApplyFilter();
        }
        
        public Link GetInfoByURL(string parse_url, string needLink)
        {
            if (net == null)
                net = new Network();

            Link link = new Link();

            string[] separator = new string[1] { "\n" };
            string[] needLinks = needLink.Split(separator, StringSplitOptions.RemoveEmptyEntries);

            string content = net.GetContentFromURL(parse_url);
            if (net.status != xNet.HttpStatusCode.OK)
            {
                link.errorHTTP = true;
                return link;
            }

            var htmlDoc = new HtmlAgilityPack.HtmlDocument();
            htmlDoc.LoadHtml(content);

            Uri url;
            try
            {
                url = new Uri(parse_url);
            }
            catch (Exception e)
            {
                link.errorHTTP = true;
                return link;
            }

            string rel = "";
            string href = "";


            //Поиск nofollow ссылок
            var A = htmlDoc.DocumentNode.SelectNodes("//a");
            if (A != null)
            {
                for (int i = 0; i < A.Count; i++)
                {
                    href = "";
                    rel = "";
                    for (int z = 0; z < A[i].Attributes.Count; z++)
                    {
                        if (A[i].Attributes[z].Name.ToLower() == "href")
                            href = A[i].Attributes[z].Value.ToLower();
                        if (A[i].Attributes[z].Name.ToLower() == "rel")
                            rel = A[i].Attributes[z].Value.ToLower();
                    }
                    for (int l = 0; l < needLinks.Length; l++)
                    {
                        if (AnalyzeURL(href, needLinks[l]))         //Если нашли искомую ссылку
                        {
                            if (link.backURL == "")
                            {
                                link.backURL = href;
                                link.indexPosition = A[i].StreamPosition;
                                link.anchor = A[i].InnerText;
                                link.anchor = CheckTZ2(link.anchor, link.backURL);
                            }
                            if (rel == "nofollow")
                                link.nofollow = "Да";
                            else
                            {
                                if (link.nofollow != "Да")
                                    link.nofollow = "Нет";
                            }
                            //break;                              //Останавливаем перебор ссылок
                        }
                    }
                }
            }
            if (link.backURL == "")
            {
                link.allGood = false;
                return link;
            }

            //Ссылки в теге <Noindex>
            var noindexA = htmlDoc.DocumentNode.SelectNodes("//noindex//a");
            if (noindexA != null)
            {
                for (int i = 0; i < noindexA.Count; i++)
                {
                    for (int z = 0; z < noindexA[i].Attributes.Count; z++)
                    {
                        if (noindexA[i].Attributes[z].Name.ToLower() == "href")
                            href = noindexA[i].Attributes[z].Value.ToLower();
                    }
                    for (int l = 0; l < needLinks.Length; l++)
                    {
                        if (AnalyzeURL(href, needLinks[l]))         //Если нашли искомую ссылку
                        {
                            if (link.indexPosition != 0)
                                if (link.indexPosition == noindexA[i].StreamPosition)
                                {
                                    link.backURL = href;
                                    link.anchor = noindexA[i].InnerText;
                                    if ((link.anchor!=null)&&(link.anchor!=""))         //По ТЗ 2 №1
                                    {
                                        link.anchor = CheckTZ2(link.anchor, link.backURL);
                                    }
                                    link.noindex = "Да";
                                    break;                              //Останавливаем перебор ссылок
                                }
                        }
                    }
                }
            }
            if (link.noindex == "")
                link.noindex = "Нет";
            try
            {
                string robots__txt = net.GetRobotsFromURL(url.Scheme + "://" + url.Host + "/robots.txt");
                RobotsTxt.Robots robots = new RobotsTxt.Robots(robots__txt);
                if (robots.IsPathAllowed("yandex", url.AbsolutePath+url.Query))
                    link.noYR = "Нет";
                else
                    link.noYR = "Да";
                if (robots.IsPathAllowed("Googlebot", url.AbsolutePath + url.Query))
                    link.noGR = "Нет";
                else
                    link.noGR = "Да";

                if (robots.IsPathAllowed("*", url.AbsolutePath + url.Query))
                    link.noAll = "Нет";
                else
                    link.noAll = "Да";
            }
            catch (Exception E)
            {

            }

            return link;
        }

        public string CheckTZ2(string anchor, string backUrl)
        {
            if (Settings.Default.useReplace)
            {
                if (anchor.IndexOf("&#8203;") > 0)
                    anchor = anchor.Replace("&#8203;", "");
                if (anchor.IndexOf("&hellip;") > 0)
                    anchor = backUrl;
                if ((anchor.ToLower().IndexOf("http") >= 0) && (anchor.IndexOf("...") > 0))
                    anchor = backUrl;
            }
            return anchor;
        }

        public void GetInfoByRobots(string parse_url)
        {
            Uri url = new Uri(parse_url);
            string robots__txt = net.GetContentFromURL(url.Scheme + "://"+url.Host+"/robots.txt");
            RobotsTxt.Robots robots = new RobotsTxt.Robots(robots__txt);
            bool one = robots.IsPathAllowed("*", "/*/*/feed/*/123");
            bool twoe = robots.IsPathAllowed("*", "/sdfg/sdfg/feyed/sdfg/123");
        }

        public bool AnalyzeURL(string parse_url, string link)
        {
            parse_url = parse_url.ToLower();
            link = link.ToLower();
            string tmp = parse_url.ToLower().Replace("http://", "").Replace("https://", "");
            if (!Settings.Default.noObrabotka)
            {
                if (tmp.IndexOf("/") > 0)
                    tmp = tmp.Substring(0, tmp.IndexOf("/"));
            }
            if ((tmp == link)||(tmp == "www." + link))
                return true;
            else
                return false;
        }

        public bool AnalyzeURLWL(string parse_url, string link)
        {

            parse_url = parse_url.ToLower();
            link = link.ToLower();
            string tmp = parse_url.ToLower().Replace("http://", "").Replace("https://", "");
            if (tmp.IndexOf("/") > 0)
                tmp = tmp.Substring(0, tmp.IndexOf("/"));
            if ((tmp == link) || (tmp == "www." + link))
                return true;
            else
                return false;
        }


        public int GetUniqueByPos(int pos)
        {
            if ((_pos != null) && (pos < _pos.Length))
                return links[_pos[pos]].unique;
            return -1;
        }

        public int GetPositionOrderInList(int pos)
        {
            if ((_pos != null) && (pos < _pos.Length))
                return _pos[pos];
            else
                return -1;
        }

        void Job()
        {

        }


        public int GetCountRow()
        {
            if (_pos == null)
                return 0;
            return _pos.Length;
        }

        public void ClearFilter()
        {
            ActiveFilter = new filter();
            ApplyFilter();
        }

        public void ApplyFilter()
        {
            int count = 0;
            bool good;
            _filtered = new int[links.Count];
            for (int i = 0; i < links.Count; i++)
            {
                good = true;

                if (ActiveFilter.okAllGood)
                {
                    if (!links[i].allGood)
                        good = false;
                    if (links[i].backURL == "")
                        good = false;
                }
                if (ActiveFilter.okNofollow)
                {
                    if (links[i].nofollow != "Да")
                        good = false;
                }
                if (ActiveFilter.okNoindex)
                {
                    if (links[i].noindex != "Да")
                        good = false;
                }


                if (ActiveFilter.openRobotsAll)
                {
                    if (links[i].noAll == "Да")
                        good = false;
                }
                else
                    if (ActiveFilter.closeRobotsAll)
                    {
                        if (links[i].noAll != "Да")
                            good = false;
                    }

                if (ActiveFilter.openRobotsYandex)
                {
                    if (links[i].noYR == "Да")
                        good = false;
                }
                else
                if (ActiveFilter.closeRobotsYandex)
                {
                    if (links[i].noYR != "Да")
                        good = false;
                }

                if (ActiveFilter.openRobotsGoogle)
                {
                    if (links[i].noGR == "Да")
                        good = false;
                }
                else
                if (ActiveFilter.closeRobotsGoogle)
                {
                    if (links[i].noGR != "Да")
                        good = false;
                }

                if (ActiveFilter.openOr)
                {
                    if ((links[i].noGR == "Да") && (links[i].noYR == "Да"))
                        good = false;
                }
                else
                if (ActiveFilter.closeOr)
                {
                    if ((links[i].noGR != "Да") && (links[i].noYR != "Да"))
                        good = false;
                }


                if (good)
                {
                    _filtered[count] = i;
                    count++;
                }
            }
            Array.Resize(ref _filtered, count);
            _pos = new int[count];
            Array.Copy(_filtered, _pos, count);
        }


        public void ApplyFilterError()
        {
            int count = 0;
            bool good;
            _filtered = new int[links.Count];
            haveBroken = false;
            for (int i = 0; i < links.Count; i++)
            {
                good = true;

                if (!links[i].error)                
                    good = false;               
                else
                {
                    haveBroken = true;
                    if (good)
                    {
                        _filtered[count] = i;
                        count++;
                    }
                }
            }
            Array.Resize(ref _filtered, count);
            _pos = new int[count];
            Array.Copy(_filtered, _pos, count);
        }


        static Predicate<Link> ByURL(Link url)
        {
            return delegate (Link lnk)
            {
                return lnk.url == url.url;
            };
        }


        public void Clear()
        {
            haveBroken = false;
            links.Clear();
            _pos = new int[links.Count];
            last_num = 0;
        }

        //копия объекта
        public static T DeepClone<T>(T obj)
        {
            T objResult;
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryFormatter bf = new BinaryFormatter();
                bf.Serialize(ms, obj);
                ms.Position = 0;
                objResult = (T)bf.Deserialize(ms);
            }
            return objResult;
        }


        public string ExportCSV()
        {

            //after your loop


            return "";

        }

        public List<Link> CheckDuplicates()
        {
            //List<Link> tmp = new List<Link>(links.Count);
            List<Link> tmp = DeepClone(links);

            List<Link> answer = new List<Link>();

            int cnt = 0;
            List<int> nums = new List<int>();

            for (int i = 0; i < links.Count; i++)
            {
                cnt = i;
                while (cnt < tmp.Count)
                {
                    if ((tmp[cnt].url == links[i].url) && (tmp[cnt].unique != links[i].unique))
                    {
                        answer.Add(tmp[cnt]);
                        tmp.RemoveAt(cnt);
                    }
                    else
                        cnt++;
                }
            }
            return answer;
        }

    }
}
