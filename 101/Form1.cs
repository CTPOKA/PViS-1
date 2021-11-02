using System;
using System.Collections.Generic;
using OpenCL.Net;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.IO;
using System.Drawing.Imaging;
using System.Drawing;
using System.Diagnostics;

namespace _101
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
            Setup();
        }

        private Context _context;
        private Device _device;
        OpenCL.Net.Program program;

        private Stopwatch stopWatch = new Stopwatch();
        public void CPU()
        {
            using (FileStream imageFileStream = new FileStream(openFileDialog1.FileName, FileMode.Open))
            {
                Bitmap input = new Bitmap(imageFileStream);
                Bitmap output1 = new Bitmap(input.Width, input.Height);
                Bitmap output2 = new Bitmap(input.Width, input.Height);

                for (int x = 0; x < input.Width; x++)
                    for (int y = 0; y < input.Height; y++)
                    {
                        Color p = input.GetPixel(x, y); //Исходный пиксель

                        //Модификация синего канала
                        int blue = p.B - (p.G + p.B) / 2 + 127;
                        output1.SetPixel(x, y, Color.FromArgb(p.R, p.G, blue));

                        //Модификация желтого канала
                        float key = 1 - Math.Max(p.R, Math.Max(p.G, p.B)) / 255.0f;
                        float yellow = (p.R + p.G - 2 * (Math.Abs(p.R - p.G) + p.B) + 765) / 1275.0f;
                        blue = (int)(255 * (1 - key) * (1 - yellow));
                        output2.SetPixel(x, y, Color.FromArgb(p.R, p.G, blue));
                    }

                pictureBox2.Image = output1;
                pictureBox3.Image = output2;
            }
        }

        private void CheckErr(ErrorCode err, string name)
        {
            if (err != ErrorCode.Success)
            {
                Console.WriteLine("ERROR: " + name + " (" + err.ToString() + ")");
            }
        }
        private void ContextNotify(string errInfo, byte[] data, IntPtr cb, IntPtr userData)
        {
            Console.WriteLine("OpenCL Notification: " + errInfo);
        }
        private void Setup()
        {
            ErrorCode error;
            Platform[] platforms = Cl.GetPlatformIDs(out error);
            List<Device> devicesList = new List<Device>();

            CheckErr(error, "Cl.GetPlatformIDs");

            foreach (Platform platform in platforms)
            {
                string platformName = Cl.GetPlatformInfo(platform, PlatformInfo.Name, out error).ToString();
                Console.WriteLine("Platform: " + platformName);
                CheckErr(error, "Cl.GetPlatformInfo");

                //Поиск устройств GPU
                foreach (Device device in Cl.GetDeviceIDs(platform, DeviceType.Gpu, out error))
                {
                    CheckErr(error, "Cl.GetDeviceIDs");
                    Console.WriteLine("Device: " + device.ToString());
                    devicesList.Add(device);
                }
            }

            if (devicesList.Count <= 0)
            {
                Console.WriteLine("No devices found.");
                return;
            }

            _device = devicesList[0];

            if (Cl.GetDeviceInfo(_device, DeviceInfo.ImageSupport, out error).CastTo<Bool>() == Bool.False)
            {
                Console.WriteLine("No image support.");
                return;
            }
            _context = Cl.CreateContext(null, 1, new[] { _device }, ContextNotify, IntPtr.Zero, out error);    //Second parameter is amount of devices
            CheckErr(error, "Cl.CreateContext");

            //Загрузка и компиляция кода kernel.
            string programPath = System.Environment.CurrentDirectory + "/ImagingTest.cl";

            if (!File.Exists(programPath))
            {
                Console.WriteLine("Program doesn't exist at path " + programPath);
                return;
            }

            string programSource = File.ReadAllText(programPath);

            program = Cl.CreateProgramWithSource(_context, 1, new[] { programSource }, null, out error);

            CheckErr(error, "Cl.CreateProgramWithSource");
            //Компиляция кода kernel
            error = Cl.BuildProgram(program, 1, new[] { _device }, string.Empty, null, IntPtr.Zero);
            CheckErr(error, "Cl.BuildProgram");
            //Проверка наличия ошибок
            if (Cl.GetProgramBuildInfo(program, _device, ProgramBuildInfo.Status, out error).CastTo<BuildStatus>()
                != BuildStatus.Success)
            {
                CheckErr(error, "Cl.GetProgramBuildInfo");
                Console.WriteLine("Cl.GetProgramBuildInfo != Success");
                Console.WriteLine(Cl.GetProgramBuildInfo(program, _device, ProgramBuildInfo.Log, out error));
                return;
            }

        }
        public void GPU()
        {
            ErrorCode error;

            //Cоздание kernel
            Kernel kernel = Cl.CreateKernel(program, "imagingTest", out error);
            CheckErr(error, "Cl.CreateKernel");

            int intPtrSize = 0;
            intPtrSize = Marshal.SizeOf(typeof(IntPtr));
            //Данные RGBA изображения преобразуются в массив
            byte[] inputByteArray;
            //Буфер OpenCL где будет храниться изображение
            IMem inputImage2DBuffer;
            OpenCL.Net.ImageFormat clImageFormat = new OpenCL.Net.ImageFormat(ChannelOrder.RGBA, ChannelType.Unsigned_Int8);
            int inputImgWidth, inputImgHeight;

            int inputImgBytesSize;
            int inputImgStride;

            //Попытка открыть изображение
            using (FileStream imageFileStream = new FileStream(openFileDialog1.FileName, FileMode.Open))
                {
                    Image inputImage = Image.FromStream(imageFileStream);

                    if (inputImage == null)
                    {
                        Console.WriteLine("Unable to load input image");
                        return;
                    }

                    inputImgWidth = inputImage.Width;
                    inputImgHeight = inputImage.Height;

                    Bitmap bmpImage = new Bitmap(inputImage);
                    //Get raw pixel data of the bitmap
                    //The format should match the format of clImageFormat
                    BitmapData bitmapData = bmpImage.LockBits(new Rectangle(0, 0, bmpImage.Width, bmpImage.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    inputImgStride = bitmapData.Stride;
                    inputImgBytesSize = bitmapData.Stride * bitmapData.Height;

                    //Copy the raw bitmap data to an unmanaged byte[] array
                    inputByteArray = new byte[inputImgBytesSize];
                    Marshal.Copy(bitmapData.Scan0, inputByteArray, 0, inputImgBytesSize);
                    //Allocate OpenCL image memory buffer
                    inputImage2DBuffer = Cl.CreateImage2D(_context, MemFlags.CopyHostPtr | MemFlags.ReadOnly, clImageFormat,
                                                        (IntPtr)bitmapData.Width, (IntPtr)bitmapData.Height,
                                                        (IntPtr)0, inputByteArray, out error);
                    CheckErr(error, "Cl.CreateImage2D input");
                }
                //Unmanaged output image's raw RGBA byte[] array
                byte[] outputByteArray1 = new byte[inputImgBytesSize];
                byte[] outputByteArray2 = new byte[inputImgBytesSize];
                //Allocate OpenCL image memory buffer
                IMem outputImage2DBuffer1 = Cl.CreateImage2D(_context, MemFlags.CopyHostPtr |
                    MemFlags.WriteOnly, clImageFormat, (IntPtr)inputImgWidth,
                    (IntPtr)inputImgHeight, (IntPtr)0, outputByteArray1, out error);
                IMem outputImage2DBuffer2 = Cl.CreateImage2D(_context, MemFlags.CopyHostPtr |
                    MemFlags.WriteOnly, clImageFormat, (IntPtr)inputImgWidth,
                    (IntPtr)inputImgHeight, (IntPtr)0, outputByteArray2, out error);
                CheckErr(error, "Cl.CreateImage2D output");
                //Pass the memory buffers to our kernel function
                error = Cl.SetKernelArg(kernel, 0, (IntPtr)intPtrSize, inputImage2DBuffer);
                error |= Cl.SetKernelArg(kernel, 1, (IntPtr)intPtrSize, outputImage2DBuffer1);
                error |= Cl.SetKernelArg(kernel, 2, (IntPtr)intPtrSize, outputImage2DBuffer2);
                CheckErr(error, "Cl.SetKernelArg");

                //Create a command queue, where all of the commands for execution will be added
                CommandQueue cmdQueue = Cl.CreateCommandQueue(_context, _device, 0, out error);
                CheckErr(error, "Cl.CreateCommandQueue");

                Event clevent;
                //Copy input image from the host to the GPU.
                IntPtr[] originPtr = new IntPtr[] { (IntPtr)0, (IntPtr)0, (IntPtr)0 };    //x, y, z
                IntPtr[] regionPtr = new IntPtr[] { (IntPtr)inputImgWidth, (IntPtr)inputImgHeight, (IntPtr)1 };    //x, y, z
                IntPtr[] workGroupSizePtr = new IntPtr[] { (IntPtr)inputImgWidth, (IntPtr)inputImgHeight, (IntPtr)1 };
                error = Cl.EnqueueWriteImage(cmdQueue, inputImage2DBuffer, Bool.True,
                   originPtr, regionPtr, (IntPtr)0, (IntPtr)0, inputByteArray, 0, null, out clevent);
                CheckErr(error, "Cl.EnqueueWriteImage");
                //Execute our kernel (OpenCL code)
                error = Cl.EnqueueNDRangeKernel(cmdQueue, kernel, 2, null, workGroupSizePtr, null, 0, null, out clevent);
                CheckErr(error, "Cl.EnqueueNDRangeKernel");
                //Wait for completion of all calculations on the GPU.
                error = Cl.Finish(cmdQueue);
                CheckErr(error, "Cl.Finish");
                //Read the processed image from GPU to raw RGBA data byte[] array
                error = Cl.EnqueueReadImage(cmdQueue, outputImage2DBuffer1, Bool.True, originPtr, regionPtr, (IntPtr)0, (IntPtr)0, outputByteArray1, 0, null, out clevent);
                error |= Cl.EnqueueReadImage(cmdQueue, outputImage2DBuffer2, Bool.True, originPtr, regionPtr, (IntPtr)0, (IntPtr)0, outputByteArray2, 0, null, out clevent);
                CheckErr(error, "Cl.clEnqueueReadImage");
                //Clean up memory
                Cl.ReleaseKernel(kernel);
                Cl.ReleaseCommandQueue(cmdQueue);

                Cl.ReleaseMemObject(inputImage2DBuffer);
                Cl.ReleaseMemObject(outputImage2DBuffer1);
                Cl.ReleaseMemObject(outputImage2DBuffer2);
                //Get a pointer to our unmanaged output byte[] array
                GCHandle pinnedOutputArray1 = GCHandle.Alloc(outputByteArray1, GCHandleType.Pinned);
                IntPtr outputBmpPointer1 = pinnedOutputArray1.AddrOfPinnedObject();
                GCHandle pinnedOutputArray2 = GCHandle.Alloc(outputByteArray2, GCHandleType.Pinned);
                IntPtr outputBmpPointer2 = pinnedOutputArray2.AddrOfPinnedObject();
                //Create a new bitmap with processed data and save it to a file.
                Bitmap outputBitmap1 = new Bitmap(inputImgWidth, inputImgHeight, inputImgStride, PixelFormat.Format32bppArgb, outputBmpPointer1);
                Bitmap outputBitmap2 = new Bitmap(inputImgWidth, inputImgHeight, inputImgStride, PixelFormat.Format32bppArgb, outputBmpPointer2);

                pictureBox2.Image = outputBitmap1;
                pictureBox3.Image = outputBitmap2;

                pinnedOutputArray1.Free();
                pinnedOutputArray2.Free();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                using (FileStream imageFileStream = new FileStream(openFileDialog1.FileName, FileMode.Open))
                    pictureBox1.Image = Image.FromStream(imageFileStream);
                label4.Text = string.Format("размер изображения: {0}x{1} \nвремя выполнения: -", pictureBox1.Image.Width, pictureBox1.Image.Height);
                pictureBox2.Image = Image.FromFile(System.Environment.CurrentDirectory + "\\no-image-small.jpg");
                pictureBox3.Image = Image.FromFile(System.Environment.CurrentDirectory + "\\no-image-small.jpg");
            }
        }
        private void button4_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.FileName == "")
            {
                if (openFileDialog1.ShowDialog() == DialogResult.OK)
                {
                    using (FileStream imageFileStream = new FileStream(openFileDialog1.FileName, FileMode.Open))
                        pictureBox1.Image = Image.FromStream(imageFileStream);
                }
                else
                    return;
            }
            stopWatch.Start();
            if (radioButton1.Checked)
                CPU();
            else
                GPU();
            stopWatch.Stop();
            label4.Text = string.Format("размер изображения: {0}x{1} \nвремя выполнения: {2} мс", pictureBox1.Image.Width, pictureBox1.Image.Height, stopWatch.Elapsed.TotalMilliseconds);
            //MessageBox.Show(string.Format("Время выполнения: {0} мс", stopWatch.Elapsed.TotalMilliseconds));
            stopWatch.Reset();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            if (saveFileDialog1.ShowDialog() == DialogResult.OK)
            {
                pictureBox2.Image.Save(saveFileDialog1.FileName);
            }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            if (saveFileDialog1.ShowDialog() == DialogResult.OK)
            {
                pictureBox3.Image.Save(saveFileDialog1.FileName);
            }
        }
    }
}
