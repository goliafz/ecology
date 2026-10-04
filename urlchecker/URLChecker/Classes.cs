using System;
using System.Collections.Generic;
using System.Text;

namespace URLChecker
{
    [Serializable]
    public class Column
    {
        public string name { get; set; }
        public int width { get; set; }
        public bool visible { get; set; }
        public int position { get; set; }
        public string caption { get; set; }
    }

    [Serializable]
    public class Link
    {
        public int N_;   //№
        public int unique;   //№
        public int indexPosition;       //Позиция в потоке страницы, для сопоставления ссылки
        public string url;  //
        public string checkedURL = "";
        public bool error = false;

        string _backURL = "";

        public string backURL
        {
            get { return _backURL; }
            set { 
                try
                {
                    if (value != "")
                    {
                        Uri url = new Uri(value);
                        if (url.LocalPath.Length<=1)
                            _backURL = url.Scheme + "://" + url.Host + "/";
                        else
                            _backURL = value;
                        string[] sepa = new string[] { "://" };
                        string[] tmp = value.Split(sepa, StringSplitOptions.RemoveEmptyEntries);
                        if (tmp.Length > 2)
                            error = true;
                    }
                    else
                        _backURL = value;
                }
                catch (Exception E)
                {
                    _backURL = value;
                }

            }
        }
        public string anchor = "";

        public bool WL = false;

        public bool deleted = false;

        string _noindex;
        public string noindex
        {
            get { return _noindex; }
            set { if (value == "Да") allGood = false;
                _noindex = value;
            }
        }
        string _nofollow;
        public string nofollow
        {
            get { return _nofollow; }
            set { if (value == "Да") allGood = false;
                _nofollow = value;
            }
        }

        public bool meta_noindex;

        bool _errorHTTP;
        public bool errorHTTP      //ошибка открытия страницы
        {
            get { return _errorHTTP; }
            set { if (value) allGood = false;
                _errorHTTP = value;
            }
        }
        public bool noRobots = false; //robots отсутствует

        public string _noYR;
        public string noYR
        {
            get { return _noYR; }
            set { if (value == "Да") allGood = false;
                _noYR = value;
            }
        }

        public string _noGR;
        public string noGR
        {
            get { return _noGR; }
            set { if (value == "Да") allGood = false;
                _noGR = value;
                }
        }

        public bool allGood = true;

        string _noAll;     //для useragent = *
        public string noAll      //для useragent = *
        {
            get { return _noAll; }
            set
            {
                if (value == "Да") allGood = false;
                _noAll = value;
            }
        }

        public Link()
        {
            allGood = true;
            meta_noindex = false;
            noindex = "";
            nofollow = "";
            noGR = "";
            noYR = "";
            noAll = "";
            indexPosition = 0;
        }
    }
    
    [Serializable]
    public class filter
    {
        public bool okAllGood = false;
        public bool okNoindex = false;
        public bool okNofollow = false;
        public bool closeRobotsAll = false;
        public bool openRobotsAll = false;
        public bool closeRobotsYandex = false;
        public bool openRobotsYandex = false;
        public bool closeRobotsGoogle = false;
        public bool openRobotsGoogle = false;
        public bool closeOr = false;
        public bool openOr = false;
        public bool error = true;
    }

    public class ComparerAsc : IComparer<string>
    {
        public int Compare(string x, string y)
        {
            if ((y.Length <= 0) && (x.Length <= 0)) return 0;
            if (x.Length <= 0) return -1;
            if (y.Length <= 0) return 1;

            if (Convert.ToInt32(x).CompareTo(Convert.ToInt32(y)) == 0)
                return 0;
            else
                if (Convert.ToInt32(x).CompareTo(Convert.ToInt32(y)) > 0)
                return 1;
            else
                return -1;
        }

    }

    public class ComparerDesc : IComparer<string>
    {
        public int Compare(string x, string y)
        {
            if ((y.Length <= 0) && (x.Length <= 0)) return 0;
            if (x.Length <= 0) return -1;
            if (y.Length <= 0) return 1;

            if (Convert.ToInt32(x).CompareTo(Convert.ToInt32(y)) == 0)
                return 0;
            else
                if (Convert.ToInt32(x).CompareTo(Convert.ToInt32(y)) > 0)
                return -1;
            else
                return 1;
        }
    }

    public class StringComparerAsc : IComparer<string>
    {
        public int Compare(string x, string y)
        {
            if ((y.Length <= 0) && (x.Length <= 0)) return 0;

            if (x.CompareTo(y) == 0)
                return 0;
            else
                if (x.CompareTo(y) > 0)
                return 1;
            else
                return -1;
        }

    }

    public class StringComparerDesc : IComparer<string>
    {
        public int Compare(string x, string y)
        {
            if ((y.Length <= 0) && (x.Length <= 0)) return 0;

            if (x.CompareTo(y) == 0)
                return 0;
            else
                if (x.CompareTo(y) > 0)
                return -1;
            else
                return 1;
        }
    }




}
