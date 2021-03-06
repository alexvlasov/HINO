﻿using System;
using AForge.Video.FFMPEG;
using System.Drawing;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Text;
using System.Linq;

namespace HardsubIsNotOk
{
    class ConversionThread
    {
        public static LockBitmap frame; //the current frame where we're searching for subtitles
        public static long frameIndex; //the index of the current frame
        public static bool[,] filled; //pixels of the current frame that have been already checked

        public static List<List<Subtitle>> subtitles = new List<List<Subtitle>>(); //one array of subtitle foreach video
        public static List<Subtitle> waitForUser = new List<Subtitle>(); //subtitles that are in wait for user action

        static LockBitmap[] buffer, buffer1, buffer2; //buffer 1 & 2 are for buffering with a separated thread. "Buffer" contains the reference to the buffer in use for recognize subtitles (ref to 1 or 2)
        static int bufferSize, whatBuffer = 0; //for the buffering thread
        static bool bufferingPause, bufferingComplete;

        static Letter newLetter; //it contains the letter for the filling
        static bool notALetterFlag; //will be set true when the letter we're filling goes out of bounds

        static float idealHue = Settings.outSubtitleColor.GetHue(); //ideal hue for the outline

        /// <summary>
        /// Main thread for find letters in the videos and convert them to a Letter List.
        /// </summary>
        public static void Go()
        {
            idealHue = Settings.outSubtitleColor.GetHue();
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            Subtitle subTop = null, subBottom = null;
            Thread subRec = new Thread(RecognizeSubtitles);
            subRec.IsBackground = true;
            subRec.Start();
            subRec = new Thread(RecognizeWaitForUser);
            subRec.IsBackground = true;
            subRec.Start();

            foreach (string k in Program.videos.Keys)
            {
                frameIndex = 0;
                subtitles.Add(new List<Subtitle>());
                bufferSize = Program.videos[k].FrameRate / 2;

                buffer1 = new LockBitmap[bufferSize];
                buffer2 = new LockBitmap[bufferSize];

                Thread bufferThread = new Thread(() => Buffering(k));
                bufferThread.IsBackground = true;
                bufferingComplete = false;
                bufferingPause = false;
                bufferThread.Start();
                while (!bufferingPause) ;

                while (!bufferingComplete)
                {
                    buffer = whatBuffer == 0 ? buffer2 : buffer1;

                    bufferingPause = false;

                    frameIndex += buffer.Length;
                    frame = buffer[bufferSize - 1];
                    if (!Settings.ignoreTopSubtitles)
                    {
                        if (subTop == null)
                        {
                            subTop = GetSubtitleTop();
                            if (subTop != null)
                                subTop.GetStartFromBuffer(buffer);
                        }
                        else if (subTop.GetEndFromBuffer(buffer))
                        {
                            if (subTop.endFrame - subTop.startFrame >= Settings.minSubLength)
                                subtitles.Last().Add(subTop);
                            subTop = GetSubtitleTop();
                            if (subTop != null)
                                subTop.GetStartFromBuffer(buffer);
                        }
                    }

                    if (subBottom == null)
                    {
                        subBottom = GetSubtitleBottom();
                        if (subBottom != null)
                            subBottom.GetStartFromBuffer(buffer);
                    }
                    else if (subBottom.GetEndFromBuffer(buffer))
                    {
                        if (subBottom.endFrame - subBottom.startFrame >= Settings.minSubLength)
                            subtitles.Last().Add(subBottom);
                        subBottom = GetSubtitleBottom();
                        if (subBottom != null)
                            subBottom.GetStartFromBuffer(buffer);
                    }

                    while (!bufferingPause) ;
                }
            }
            Form1.progressBar.Invoke(new Form1.EventHandle(() => Form1.progressBar.Value = 1000));

            stopWatch.Stop();
            Console.WriteLine("-------------TEMPO DI ESTRAZIONE-----------");
            Console.WriteLine(stopWatch.Elapsed);
        }
        /// <summary>
        /// Used as a thread for buffering the video in background.
        /// </summary>
        /// <param name="vIndex">Index of the video that have to be buffered</param>
        public static void Buffering(string vIndex)
        {
            Bitmap b;
            int bufferIndex;
            start:

            bufferIndex = 0;
            if (whatBuffer == 0)
            {
                while ((b = Program.videos[vIndex].ReadVideoFrame()) != null)
                {
                    try
                    {
                        buffer1[bufferIndex].source.Dispose();
                    }
                    catch { }
                    buffer1[bufferIndex] = new LockBitmap(b);
                    buffer1[bufferIndex].LockBits();
                    bufferIndex++;
                    //frameIndex++;
                    if (frameIndex % 50 == 0 && Program.videos[vIndex].FrameCount != 0)
                        Form1.progressBar.Invoke(new Form1.EventHandle(() => Form1.progressBar.Value = (int)(((float)frameIndex / Program.videos[vIndex].FrameCount) * 1000 / Program.videos.Count + Program.videos.Keys.ToList().IndexOf(vIndex) * 1000 / Program.videos.Count)));
                    if (bufferIndex == bufferSize)
                    {
                        whatBuffer = 1;
                        bufferingPause = true;
                        while (bufferingPause) ;
                        goto start;
                    }
                }
            }
            else
            {
                while ((b = Program.videos[vIndex].ReadVideoFrame()) != null)
                {
                    try
                    {
                        buffer2[bufferIndex].source.Dispose();
                    }
                    catch { }
                    buffer2[bufferIndex] = new LockBitmap(b);
                    buffer2[bufferIndex].LockBits();
                    bufferIndex++;
                    //frameIndex++;
                    if (frameIndex % 50 == 0 && Program.videos[vIndex].FrameCount != 0)
                        Form1.progressBar.Invoke(new Form1.EventHandle(() => Form1.progressBar.Value = (int)(((float)frameIndex / Program.videos[vIndex].FrameCount) * 1000 / Program.videos.Count + Program.videos.Keys.ToList().IndexOf(vIndex) * 1000 / Program.videos.Count)));
                    if (bufferIndex == bufferSize)
                    {
                        whatBuffer = 0;
                        bufferingPause = true;
                        while (bufferingPause) ;
                        goto start;
                    }
                }
            }
            bufferingPause = true;
            bufferingComplete = true;
        }

        /// <summary>
        /// Using a neural network to convert the letters extracted from the main thread from pixel array to strings.
        /// It require user action if the network fail or the word isn't contained in dictionary
        /// </summary>
        public static void RecognizeSubtitles()
        {
            int subIndex = 0;
            int vIndex = 0;
            while (true)
            {
                while (subtitles[vIndex].Count <= subIndex)
                {
                    if (subtitles.Count > vIndex + 1)
                    {
                        Form1.progressBar.Invoke(new Form1.EventHandle(() => Form1.SetRecognitionPerc(vIndex, subtitles[vIndex].Count, subtitles[vIndex].Count)));
                        vIndex++;
                        subIndex = 0;
                        //break;
                    }
                }
                WordNotFound.Result exit;
                Form1.progressBar.Invoke(new Form1.EventHandle(() => Form1.SetRecognitionPerc(vIndex, subIndex , subtitles[vIndex].Count)));

                StringBuilder converted = new StringBuilder();
                int wordStart = 0;
                for (int lineIndex = 0; lineIndex < subtitles[vIndex][subIndex].lines.Count; lineIndex++)
                {
                    Subtitle.Line line = subtitles[vIndex][subIndex].lines[lineIndex];
                    for (int c = 0; c < line.letters.Count; c++)
                    {
                        if (line.letters[c] is Space)
                        {
                            if (!TryToCorrectWithDictionary(subtitles[vIndex][subIndex], lineIndex, wordStart, c, converted))
                            {
                                if (Settings.dictionaryMode)
                                {
                                    waitForUser.Add(subtitles[vIndex][subIndex]);
                                    //subtitles.RemoveAt(subIndex);
                                    subIndex++;
                                    goto skipSub;
                                }
                                else
                                {
                                    exit = ShowDictionaryDialog(subtitles[vIndex][subIndex], lineIndex, wordStart, c, converted);
                                    switch (exit)
                                    {
                                        case WordNotFound.Result.skipSub:
                                            subtitles[vIndex].RemoveAt(subIndex);
                                            goto skipSub;
                                        case WordNotFound.Result.subChanged:
                                            goto skipSub;
                                        case WordNotFound.Result.subRewrited:
                                            subIndex++;
                                            goto skipSub;
                                    }
                                }
                            }
                            converted.Append(' ');
                            wordStart = c + 1;
                            continue;
                        }
                        line.letters[c].GenerateArray();
                        line.letters[c].Recognize();

                        if (line.letters[c].firstOverSecondCorrectness > 0.5) //parametrizzabile
                            line.letters[c].secondChoice = null;

                        if (Settings.dictionaryMode)
                        {
                            if (line.letters[c].error > Settings.maxDictionaryError || line.letters[c].firstOverSecondCorrectness < Settings.minDictionaryCorrectness)
                            {
                                waitForUser.Add(subtitles[vIndex][subIndex]);
                                //subtitles.RemoveAt(subIndex);
                                subIndex++;
                                goto skipSub;
                            }
                        }
                        else
                        {
                            if (line.letters[c].error > Settings.maxError || line.letters[c].firstOverSecondCorrectness < Settings.minCorrectness)
                            {
                                GuessLetter.Result e = ShowCorrectLetterDialog(subtitles[vIndex][subIndex], lineIndex, c);
                                switch (e)
                                {
                                    case GuessLetter.Result.notALetter:
                                        line.letters.RemoveAt(c);
                                        c--;
                                        continue;
                                    case GuessLetter.Result.skipSub:
                                        subtitles[vIndex].RemoveAt(subIndex);
                                        goto skipSub;
                                    case GuessLetter.Result.subChanged:
                                        goto skipSub;
                                    case GuessLetter.Result.subRewrited:
                                        subIndex++;
                                        goto skipSub;

                                }
                            }
                        }

                        if (line.letters[c].value != "")
                        {
                            if (!IsLetter(line.letters[c].value))
                            {
                                if (!TryToCorrectWithDictionary(subtitles[vIndex][subIndex], lineIndex, wordStart, c, converted))
                                {
                                    if (Settings.dictionaryMode)
                                    {
                                        waitForUser.Add(subtitles[vIndex][subIndex]);
                                        //subtitles.RemoveAt(subIndex);
                                        subIndex++;
                                        goto skipSub;
                                    }
                                    else
                                    {
                                        exit = ShowDictionaryDialog(subtitles[vIndex][subIndex], lineIndex, wordStart, c, converted);
                                        switch (exit)
                                        {
                                            case WordNotFound.Result.skipSub:
                                                subtitles[vIndex].RemoveAt(subIndex);
                                                goto skipSub;
                                            case WordNotFound.Result.subChanged:
                                                goto skipSub;
                                            case WordNotFound.Result.subRewrited:
                                                subIndex++;
                                                goto skipSub;
                                        }
                                    }
                                }
                                wordStart = c + 1;
                            }
                            converted.Append(line.letters[c].value);
                        }
                    }
                    if (!TryToCorrectWithDictionary(subtitles[vIndex][subIndex], lineIndex, wordStart, line.letters.Count, converted))
                    {
                        if (Settings.dictionaryMode)
                        {
                            waitForUser.Add(subtitles[vIndex][subIndex]);
                            //subtitles.RemoveAt(subIndex);
                            subIndex++;
                            goto skipSub;
                        }
                        else
                        {
                            exit = ShowDictionaryDialog(subtitles[vIndex][subIndex], lineIndex, wordStart, line.letters.Count, converted);
                            switch (exit)
                            {
                                case WordNotFound.Result.skipSub:
                                    subtitles[vIndex].RemoveAt(subIndex);
                                    goto skipSub;
                                case WordNotFound.Result.subChanged:
                                    goto skipSub;
                                case WordNotFound.Result.subRewrited:
                                    subIndex++;
                                    goto skipSub;
                            }
                        }
                    }
                    if (converted.Length != 0 && converted[converted.Length - 1] != '\n')
                        converted.Append('\n');
                    wordStart = 0;
                }
                if (converted.Length == 0)
                {
                    subtitles[vIndex].RemoveAt(subIndex);
                }
                else if (subIndex > 0 && converted.ToString() == subtitles[vIndex][subIndex - 1].value && subtitles[vIndex][subIndex].startFrame - 1 <= subtitles[vIndex][subIndex - 1].endFrame)
                {
                    subtitles[vIndex][subIndex - 1].endFrame = subtitles[vIndex][subIndex].endFrame;
                    subtitles[vIndex].RemoveAt(subIndex);
                }
                else
                {
                    //Console.Write(converted);
                    subtitles[vIndex][subIndex].value = converted.ToString();
                    subIndex++;
                }
                skipSub:;
            }
        }
        /// <summary>
        /// Like the recognizeSubtitles, but it works on a separated list of the subtitles that require user intervent.
        /// This thread is actived by user with the "Dictionary Mode" to prevent the pausing of the recognition while waiting the user.
        /// </summary>
        public static void RecognizeWaitForUser() 
        {
            while (true)
            {
                while (waitForUser.Count == 0) ;
                Thread.Sleep(20);

                WordNotFound.Result exit;

                StringBuilder converted = new StringBuilder();
                int wordStart = 0;
                for (int lineIndex = 0; lineIndex < waitForUser[0].lines.Count; lineIndex++)
                {
                    Subtitle.Line line = waitForUser[0].lines[lineIndex];
                    for (int c = 0; c < line.letters.Count; c++)
                    {
                        if (line.letters[c] is Space)
                        {
                            if (!TryToCorrectWithDictionary(waitForUser[0], lineIndex, wordStart, c, converted))
                            {
                                exit = ShowDictionaryDialog(waitForUser[0], lineIndex, wordStart, c, converted);
                                switch (exit)
                                {
                                    case WordNotFound.Result.skipSub:
                                        //subtitles.Remove(waitForUser[0]);
                                        waitForUser.RemoveAt(0);
                                        goto skipSub;
                                    case WordNotFound.Result.subChanged:
                                        goto skipSub;
                                    case WordNotFound.Result.subRewrited:
                                        waitForUser.RemoveAt(0);
                                        goto skipSub;
                                }
                            }
                            converted.Append(' ');
                            wordStart = c + 1;
                            continue;
                        }
                        line.letters[c].GenerateArray();
                        line.letters[c].Recognize();

                        if (line.letters[c].firstOverSecondCorrectness > 0.5) //parametrizzabile
                            line.letters[c].secondChoice = null;
                        
                        if (line.letters[c].error > Settings.maxDictionaryError || line.letters[c].firstOverSecondCorrectness < Settings.minDictionaryCorrectness)
                        {
                            GuessLetter.Result e = ShowCorrectLetterDialog(waitForUser[0], lineIndex, c);
                            switch (e)
                            {
                                case GuessLetter.Result.notALetter:
                                    line.letters.RemoveAt(c);
                                    c--;
                                    continue;
                                case GuessLetter.Result.skipSub:
                                    //subtitles.Remove(waitForUser[0]);
                                    waitForUser.RemoveAt(0);
                                    goto skipSub;
                                case GuessLetter.Result.subChanged:
                                    goto skipSub;
                                case GuessLetter.Result.subRewrited:
                                    waitForUser.RemoveAt(0);
                                    goto skipSub;

                            }
                        }

                        if (line.letters[c].value != "")
                        {
                            if (!IsLetter(line.letters[c].value))
                            {
                                if (!TryToCorrectWithDictionary(waitForUser[0], lineIndex, wordStart, c, converted))
                                {
                                    exit = ShowDictionaryDialog(waitForUser[0], lineIndex, wordStart, c, converted);
                                    switch (exit)
                                    {
                                        case WordNotFound.Result.skipSub:
                                            //subtitles.Remove(waitForUser[0]);
                                            waitForUser.RemoveAt(0);
                                            goto skipSub;
                                        case WordNotFound.Result.subChanged:
                                            goto skipSub;
                                        case WordNotFound.Result.subRewrited:
                                            waitForUser.RemoveAt(0);
                                            goto skipSub;
                                    }
                                }
                                wordStart = c + 1;
                            }
                            converted.Append(line.letters[c].value);
                        }
                    }
                    if (!TryToCorrectWithDictionary(waitForUser[0], lineIndex, wordStart, waitForUser[0].lines.Count, converted))
                    {
                        exit = ShowDictionaryDialog(waitForUser[0], lineIndex, wordStart, waitForUser[0].lines.Count, converted);
                        switch (exit)
                        {
                            case WordNotFound.Result.skipSub:
                                //subtitles.Remove(waitForUser[0]);
                                waitForUser.RemoveAt(0);
                                goto skipSub;
                            case WordNotFound.Result.subChanged:
                                goto skipSub;
                            case WordNotFound.Result.subRewrited:
                                waitForUser.RemoveAt(0);
                                goto skipSub;
                        }
                    }
                    if (converted.Length != 0 && converted[converted.Length - 1] != '\n')
                        converted.Append('\n');
                    wordStart = 0;
                }
                //int subIndex = subtitles[vIndex].IndexOf(waitForUser[0]);
                if (converted.Length == 0)
                {
                    //subtitles.Remove(waitForUser[0]);
                    waitForUser.RemoveAt(0);
                }
                //else if (subIndex > 0 && converted.ToString() == subtitles[vIndex][subIndex - 1].value && subtitles[vIndex][subIndex].startFrame - 1 <= subtitles[vIndex][subIndex - 1].endFrame)
                //{
                //    subtitles[vIndex][subIndex - 1].endFrame = subtitles[vIndex][subIndex].endFrame;
                    //subtitles.Remove(waitForUser[0]);
                //    waitForUser[0].value = "";
                //    waitForUser.RemoveAt(0);
                //}
                else
                {
                    //Console.Write(converted);
                    waitForUser[0].value = converted.ToString();
                    waitForUser.RemoveAt(0);
                }
                skipSub:;
            }
        }

        /// <summary>
        /// Check if the a string contains only character A-Z, a-z.
        /// Used for split the words
        /// </summary>
        public static bool IsLetter(string s)
        {
            foreach (char c in s)
                if (!char.IsLetter(c))
                    return false;
            return true;
        }
        /// <summary>
        /// It shows the alert window for correcting a not well recognized letter.
        /// </summary>
        /// <param name="sub">Subtitle that contains the letter</param>
        /// <param name="line">Line index</param>
        /// <param name="letter">Letter index</param>
        /// <returns>The result of the alert window</returns>
        public static GuessLetter.Result ShowCorrectLetterDialog(Subtitle sub, int line, int letter)
        {
            Letter l = sub.lines[line].letters[letter];

            GuessLetter alert = new GuessLetter(sub, l);
            alert.StartPosition = FormStartPosition.CenterScreen;
            alert.ShowDialog();

            switch (alert.result)
            {
                case GuessLetter.Result.incorrect:
                    if (alert.correction == l.value)
                        goto case GuessLetter.Result.correct;

                    if (!Settings.learningDisabled)
                    {
                        Program.examples.Add(l);

                        bool newNet = !Program.neuralNetwork.ContainsKey(alert.correction);
                        if (newNet)
                            Program.neuralNetwork.Add(alert.correction, new Network(alert.correction, 24 * 24, Settings.nnSize, Settings.nnSize));

                        Program.AddLearningThread(l.value, alert.correction);
                        l.value = alert.correction;

                        if (newNet && Program.neuralNetwork.Count >= Settings.maxLearningThreads)
                            Thread.Sleep(1000);
                    }
                    break;

                case GuessLetter.Result.correct:
                    Program.examples.Add(l);
                    if (!Settings.learningDisabled)
                        Program.AddLearningThread(l.value, l.secondChoice);
                    break;

                case GuessLetter.Result.subRewrited:
                    sub.value = alert.correction;
                    break;
            }
            return alert.result;
        }
        /// <summary>
        /// It shows the alert window for correcting a word that isn't contained in dictionary.
        /// </summary>
        /// <param name="sub">Subtitle that contains the word</param>
        /// <param name="line">Line index</param>
        /// <param name="start">First character of the word index</param>
        /// <param name="end">Last character of the word index</param>
        /// <param name="converted">It contains the string that is going to be converted from the subtitle</param>
        /// <returns>The result of the alert window</returns>
        public static WordNotFound.Result ShowDictionaryDialog(Subtitle sub, int line, int start, int end, StringBuilder converted)
        {
            string word = "";
            for (int c = start; c < end; c++)
                word += sub.lines[line].letters[c].value;

            WordNotFound alert = new WordNotFound(sub, line, start, end);
            alert.ShowDialog();

            switch (alert.result)
            {
                case WordNotFound.Result.incorrect:
                    string newWord = "";
                    for (int c = start; c < end; c++)
                    {
                        string corrected = alert.correction[c - start].Text;
                        newWord += corrected;
                        if (sub.lines[line].letters[c].value != corrected)
                        {
                            string wrong = sub.lines[line].letters[c].value;
                            sub.lines[line].letters[c].value = corrected;
                            if (!Settings.learningDisabled)
                            {
                                Program.examples.Add(sub.lines[line].letters[c]);

                                bool newNet = !Program.neuralNetwork.ContainsKey(corrected);
                                if (newNet)
                                    Program.neuralNetwork.Add(corrected, new Network(corrected, 24 * 24, Settings.nnSize, Settings.nnSize));

                                Program.AddLearningThread(wrong, corrected);
                            }
                        }
                    }
                    converted.Append(newWord);
                    break;
                case WordNotFound.Result.incorrectWithoutLearning:
                    string n = "";
                    for (int c = start; c < end; c++)
                    {
                        string corrected = alert.correction[c - start].Text;
                        n += corrected;

                    }
                    converted.Append(n);
                    break;
                case WordNotFound.Result.add:
                    converted.Append(word);
                    Program.AddToDictionary(word);
                    break;
                case WordNotFound.Result.properName:
                    converted.Append(word);
                    Program.AddToProperNames(word);
                    break;
                case WordNotFound.Result.dontCare:
                    converted.Append(word);
                    break;
                case WordNotFound.Result.subRewrited:
                    sub.value = alert.subRewrited;
                    break;
            }
            return alert.result;
        }
        public static bool TryToCorrectWithDictionary(Subtitle sub, int line, int start, int end, StringBuilder converted)
        {
            string word = "";
            try
            {
                for (int c = start; c < end; c++)
                    word += sub.lines[line].letters[c].value;
            }
            catch
            {

            }
            if (word.Length > 0 && !Program.FindWord(word) && !Program.FindName(word))
            {
                converted = converted.Remove(converted.Length - word.Length, word.Length);
                word = "";

                Dictionary<string, Letter> alternatives = new Dictionary<string, Letter>();

                for (int c = start; c < end; c++) //provo a correggere una lettera
                {
                    if (sub.lines[line].letters[c].secondChoice != null && sub.lines[line].letters[c].secondChoice != "")
                    {

                        string newWord = word + sub.lines[line].letters[c].secondChoice;
                        for (int d = c + 1; d < end; d++)
                            newWord += sub.lines[line].letters[d].value;
                        if (Program.FindWord(newWord) || Program.FindName(newWord))
                            alternatives.Add(newWord, sub.lines[line].letters[c]);
                    }
                    word += sub.lines[line].letters[c].value;
                }
                if(alternatives.Count > 0)
                {
                    float min = 1;
                    string index = "";
                    foreach(KeyValuePair<string, Letter> alt in alternatives)
                    {
                        if(alt.Value.firstOverSecondCorrectness < min)
                        {
                            min = alt.Value.firstOverSecondCorrectness;
                            index = alt.Key;
                        }
                    }
                    string wrong = alternatives[index].value;
                    string corrected = alternatives[index].secondChoice;

                    alternatives[index].value = corrected;

                    if (!Settings.learningDisabled)
                    {
                        //Program.examples.Add(alternatives[index]);
                        Program.AddLearningThread(wrong, corrected);
                    }
                    converted.Append(index);
                    return true;
                }
                return false;
            }
            return true;
        }
        
        /// <summary>
        /// Check the frame pixel per pixel for find and extract the letters, in the bottom bound
        /// </summary>
        /// <returns>Extracted subtitle if found.</returns>
        static Subtitle GetSubtitleBottom()
        {
            filled = new bool[frame.Width, frame.Height];
            Subtitle subtitle = null;
            int x, y;
            for (x = 0; x < frame.Width; x += 2)
            {
                for (y = Settings.cutBottom; y < frame.Height; y += 2)
                {
                    if (!IsFilled(new Coord(x, y)) && ColorDiff(frame.GetPixel(x, y), Settings.subColor) < Settings.newCharacterThreshold) //FIX BASSA PRIORITA': ignora la colonna subito a sx nel filling 
                    {
                        Letter get = GetLetter(x, y);
                        if (get != null)
                        {
                            if (subtitle == null)
                                subtitle = new Subtitle();
                            if (get.pixels.Count < Settings.maxCharPixelSize)
                                subtitle.AddLetter(get);
                            else
                                subtitle.discardedPixels.AddRange(get.pixels);
                        }
                    }
                }
            }
            if (subtitle != null)
                ValidateSub(ref subtitle);

            return subtitle;
        }
        /// <summary>
        /// Check the frame pixel per pixel for find and extract the letters, in the top bound
        /// </summary>
        /// <returns>Extracted subtitle if found.</returns>
        static Subtitle GetSubtitleTop()
        {
            filled = new bool[frame.Width, frame.Height];
            Subtitle subtitle = null;
            int x, y;
            for (x = 0; x < frame.Width; x += 2)
            {
                for (y = 0; y < Settings.cutTop; y += 2)
                {
                    if (!IsFilled(new Coord(x, y)) && ColorDiff(frame.GetPixel(x, y), Settings.subColor) < Settings.newCharacterThreshold) 
                    {
                        Letter get = GetLetter(x, y);
                        if (get != null)
                        {
                            if (subtitle == null)
                                subtitle = new Subtitle()
                                {
                                    top = true
                                };
                            subtitle.AddLetter(get);
                        }
                    }
                }
            }
            if (subtitle != null)
                ValidateSub(ref subtitle);

            return subtitle;
        }

        /// <summary>
        /// For removing from the subtitle the letters that don't respect some filters setted by user
        /// </summary>
        /// <param name="subtitle">The reference to the subtitle that have to be validated</param>
        public static void ValidateSub(ref Subtitle subtitle)
        {
            for (int l = 0; l < subtitle.lines.Count; l++)
            {
                for (int i = 0; i < subtitle.lines[l].letters.Count; i++)
                {
                    if (subtitle.lines[l].letters[i].pixels.Count < Settings.minCharPixelSize) //|| subtitle.lines[l].letters[i].pixels.Count > Settings.maxCharPixelSize)
                    {
                        subtitle.discardedPixels.AddRange(subtitle.lines[l].letters[i].pixels);
                        subtitle.lines[l].letters.RemoveAt(i);
                        if (subtitle.lines[l].letters.Count != 0)
                            subtitle.lines[l].RecalcCoords();
                        else
                        {
                            subtitle.lines.RemoveAt(l);
                            l--;
                            goto next;
                        }
                    }
                }

                if (Settings.discardNonCenteredLines)
                {
                    while (Math.Abs(frame.Width - subtitle.lines[l].xMax - subtitle.lines[l].xMin) > Settings.nonCenteredThreshold)
                    {
                        if (subtitle.lines[l].letters.Count < 2)
                        {
                            subtitle.lines.RemoveAt(l);
                            l--;
                            break;
                        }
                        else if (frame.Width - subtitle.lines[l].xMax > subtitle.lines[l].xMin)
                        {
                            subtitle.discardedPixels.AddRange(subtitle.lines[l].letters[0].pixels);
                            subtitle.lines[l].letters.RemoveAt(0);
                            subtitle.lines[l].RecalcCoords();
                        }
                        else
                        {
                            subtitle.discardedPixels.AddRange(subtitle.lines[l].letters[subtitle.lines[l].letters.Count - 1].pixels);
                            subtitle.lines[l].letters.RemoveAt(subtitle.lines[l].letters.Count - 1);
                            subtitle.lines[l].RecalcCoords();
                        }
                    }
                    if (subtitle.lines.Count == 0)
                    {
                        subtitle = null;
                        break;
                    }
                }
                else if (Settings.discardNonPassingThroughTheCenterLines)
                {
                    if (subtitle.lines[l].xMax < frame.Width / 2 || subtitle.lines[l].xMin > frame.Width / 2)
                    {
                        subtitle.lines.RemoveAt(l);
                        if (subtitle.lines.Count == 0)
                        {
                            subtitle = null;
                            break;
                        }
                        l--;
                    }
                }
                next:;
            }
        }

        /// <summary>
        /// Get the letter using flood fill algorithm from a point 
        /// Return null if the letter doesn't respect some thresholds
        /// </summary>
        static Letter GetLetter(int x, int y)
        {
            newLetter = new Letter();
            notALetterFlag = false;
            Coord cStart = new Coord(x, y);
            diff = 0; tot = 0;
            Fill(cStart);
            float letter = diff / tot;
            if (notALetterFlag || letter > Settings.outlineThreshold)
                return null;
            return newLetter;
        }
        /// <summary>
        /// Flood fill algorithm
        /// </summary>
        /// <param name="p"></param>
        static void Fill(Coord p)
        {
            Queue<Coord> q = new Queue<Coord>();
            filled[p.x, p.y] = true;
            newLetter.AddPixel(p);
            q.Enqueue(p);
            while (q.Count > 0)
            {
                p = q.Dequeue();

                if (!IsFilled(p.Top) && IsValid(p.Top))
                {
                    filled[p.Top.x, p.Top.y] = true;
                    newLetter.AddPixel(p.Top);
                    q.Enqueue(p.Top);
                }
                if (!IsFilled(p.Bottom) && IsValid(p.Bottom))
                {
                    filled[p.Bottom.x, p.Bottom.y] = true;
                    newLetter.AddPixel(p.Bottom);
                    q.Enqueue(p.Bottom);
                }
                if (!IsFilled(p.Left) && IsValid(p.Left))
                {
                    filled[p.Left.x, p.Left.y] = true;
                    newLetter.AddPixel(p.Left);
                    q.Enqueue(p.Left);
                }
                if (!IsFilled(p.Right) && IsValid(p.Right))
                {
                    filled[p.Right.x, p.Right.y] = true;
                    newLetter.AddPixel(p.Right);
                    q.Enqueue(p.Right);
                }
                
                if (!IsFilled(p.TopLeft) && IsValid(p.TopLeft))
                {
                    filled[p.TopLeft.x, p.TopLeft.y] = true;
                    newLetter.AddPixel(p.TopLeft);
                    q.Enqueue(p.TopLeft);
                }
                if (!IsFilled(p.TopRight) && IsValid(p.TopRight))
                {
                    filled[p.TopRight.x, p.TopRight.y] = true;
                    newLetter.AddPixel(p.TopRight);
                    q.Enqueue(p.TopRight);
                }
                if (!IsFilled(p.BottomLeft) && IsValid(p.BottomLeft))
                {
                    filled[p.BottomLeft.x, p.BottomLeft.y] = true;
                    newLetter.AddPixel(p.BottomLeft);
                    q.Enqueue(p.BottomLeft);
                }
                if (!IsFilled(p.BottomRight) && IsValid(p.BottomRight))
                {
                    filled[p.BottomRight.x, p.BottomRight.y] = true;
                    newLetter.AddPixel(p.BottomRight);
                    q.Enqueue(p.BottomRight);
                }
            }
        }
        static bool IsFilled(Coord point)
        {
            return point.x < 0 || point.x >= filled.GetLength(0) || point.y < 0 || point.y >= filled.GetLength(1) || filled[point.x, point.y];
        }

        static float diff; //where the variance in the outline is stored
        static int tot;
        /// <summary>
        /// Checks if a pixel is a valid letter pixel and eventually update the variance in the outline
        /// </summary>
        static bool IsValid(Coord coord)
        {
            if (IsOut(coord.x + Settings.outlineWidth, coord.y + Settings.outlineWidth) || IsOut(coord.x - Settings.outlineWidth, coord.y - Settings.outlineWidth))
            {
                notALetterFlag = true;
                return false;
            }
            Color c1 = frame.GetPixel(coord.x, coord.y);
            Color c2 = Settings.subColor;
            bool valid = ColorDiff(c1, c2) < Settings.sameCharacterThreshold;
            if (valid)
            {
                //CHECKING THE OUTLINE

                if (Settings.whiteAndBlack)
                {
                    //TOP
                    Coord edge = coord.Top;
                    for (int c = 0; c < Settings.outlineWidth; c++)
                    {
                        c1 = frame.GetPixel(edge.x, edge.y);
                        if (ColorDiff(c1, c2) < Settings.sameCharacterThreshold)
                            break;
                        if (newLetter.outlinePixels.Add(edge))
                        {
                            //filled[edge.x, edge.y] = true;
                            byte smaller = Math.Min(c1.R, Math.Min(c1.G, c1.B));
                            c1 = Color.FromArgb(255, c1.R - smaller, c1.G - smaller, c1.B - smaller);
                            diff += ColorDiff(c1, Color.Black);
                            tot++;
                        }
                        edge = edge.Top;
                    }

                    //BOTTOM
                    edge = coord.Bottom;
                    for (int c = 0; c < Settings.outlineWidth; c++)
                    {
                        c1 = frame.GetPixel(edge.x, edge.y);
                        if (ColorDiff(c1, c2) < Settings.sameCharacterThreshold)
                            break;
                        if (newLetter.outlinePixels.Add(edge))
                        {
                            //filled[edge.x, edge.y] = true;
                            byte smaller = Math.Min(c1.R, Math.Min(c1.G, c1.B));
                            c1 = Color.FromArgb(255, c1.R - smaller, c1.G - smaller, c1.B - smaller);
                            diff += ColorDiff(c1, Color.Black);
                            tot++;
                        }
                        edge = edge.Bottom;
                    }

                    //RIGHT
                    edge = coord.Right;
                    for (int c = 0; c < Settings.outlineWidth; c++)
                    {
                        c1 = frame.GetPixel(edge.x, edge.y);
                        if (ColorDiff(c1, c2) < Settings.sameCharacterThreshold)
                            break;
                        if (newLetter.outlinePixels.Add(edge))
                        {
                            //filled[edge.x, edge.y] = true;
                            byte smaller = Math.Min(c1.R, Math.Min(c1.G, c1.B));
                            c1 = Color.FromArgb(255, c1.R - smaller, c1.G - smaller, c1.B - smaller);
                            diff += ColorDiff(c1, Color.Black);
                            tot++;
                        }
                        edge = edge.Right;
                    }

                    //LEFT
                    edge = coord.Left;
                    for (int c = 0; c < Settings.outlineWidth; c++)
                    {
                        c1 = frame.GetPixel(edge.x, edge.y);
                        if (ColorDiff(c1, c2) < Settings.sameCharacterThreshold)
                            break;
                        if (newLetter.outlinePixels.Add(edge))
                        {
                            //filled[edge.x, edge.y] = true;
                            byte smaller = Math.Min(c1.R, Math.Min(c1.G, c1.B));
                            c1 = Color.FromArgb(255, c1.R - smaller, c1.G - smaller, c1.B - smaller);
                            diff += ColorDiff(c1, Color.Black);
                            tot++;
                        }
                        edge = edge.Left;
                    }
                }
                else
                {
                    float hue;
                    //TOP
                    Coord edge = coord.Top;
                    for (int c = 0; c < Settings.outlineWidth; c++)
                    {
                        c1 = frame.GetPixel(edge.x, edge.y);
                        if (ColorDiff(c1, c2) < Settings.sameCharacterThreshold)
                            break;
                        if (newLetter.outlinePixels.Add(edge))
                        {
                            //filled[edge.x, edge.y] = true;
                            hue = frame.GetPixel(edge.x, edge.y).GetHue();
                            diff += GetHueDistance(idealHue, hue);
                            tot++;
                        }
                        edge = edge.Top;
                    }

                    //BOTTOM
                    edge = coord.Bottom;
                    for (int c = 0; c < Settings.outlineWidth; c++)
                    {
                        c1 = frame.GetPixel(edge.x, edge.y);
                        if (ColorDiff(c1, c2) < Settings.sameCharacterThreshold)
                            break;
                        if (newLetter.outlinePixels.Add(edge))
                        {
                            //filled[edge.x, edge.y] = true;
                            hue = frame.GetPixel(edge.x, edge.y).GetHue();
                            diff += GetHueDistance(idealHue, hue);
                            tot++;
                        }
                        edge = edge.Bottom;
                    }

                    //RIGHT
                    edge = coord.Right;
                    for (int c = 0; c < Settings.outlineWidth; c++)
                    {
                        c1 = frame.GetPixel(edge.x, edge.y);
                        if (ColorDiff(c1, c2) < Settings.sameCharacterThreshold)
                            break;
                        if (newLetter.outlinePixels.Add(edge))
                        {
                            //filled[edge.x, edge.y] = true;
                            hue = frame.GetPixel(edge.x, edge.y).GetHue();
                            diff += GetHueDistance(idealHue, hue);
                            tot++;
                        }
                        edge = edge.Right;
                    }

                    //LEFT
                    edge = coord.Left;
                    for (int c = 0; c < Settings.outlineWidth; c++)
                    {
                        c1 = frame.GetPixel(edge.x, edge.y);
                        if (ColorDiff(c1, c2) < Settings.sameCharacterThreshold)
                            break;
                        if (newLetter.outlinePixels.Add(edge))
                        {
                            //filled[edge.x, edge.y] = true;
                            hue = frame.GetPixel(edge.x, edge.y).GetHue();
                            diff += GetHueDistance(idealHue, hue);
                            tot++;
                        }
                        edge = edge.Left;
                    }
                }
                return true;
            }

            return false;
        }
        /// <summary>
        /// distance between two hues:
        /// </summary>
        static float GetHueDistance(float hue1, float hue2)
        {
            float d = Math.Abs(hue1 - hue2);
            return d > 180 ? 360 - d : d;
        }
        /// <summary>
        /// distance in RGB space
        /// </summary>
        public static int ColorDiff(Color c1, Color c2)
        {
            return (int)Math.Sqrt((c1.R - c2.R) * (c1.R - c2.R)
                                   + (c1.G - c2.G) * (c1.G - c2.G)
                                   + (c1.B - c2.B) * (c1.B - c2.B));
        }
        /// <summary>
        /// Check if a pixel is out of bounds
        /// </summary>
        static bool IsOut(int x, int y)
        {
            return x < 0 || y < 0 || x >= frame.Width || y >= frame.Height || (y < Settings.cutBottom && y > Settings.cutTop);
        }

    }
}